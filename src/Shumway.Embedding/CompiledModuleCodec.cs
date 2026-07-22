using System.Text;
using Shumway.Builtins;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Serializes / deserializes a <see cref="CompiledModule"/> to a portable byte
/// buffer. The encoded form is process-independent: every operand that holds
/// an interned id (atom, functor, builtin) is rewritten as the corresponding
/// name (or name + arity) so the loader can remap into the running process's
/// atom / functor / builtin tables.
///
/// <para>The codec walks each predicate's bytecode using
/// <see cref="OpcodeTable"/> to drive operand semantics — no hand-coded
/// per-opcode logic — which means new opcodes pick up correct codec behaviour
/// the moment they're added to the operand-kind catalog.</para>
///
/// <para>The codec is intentionally separate from the bundle format: a bundle
/// embeds the codec's bytes inside one of its per-entry blobs, but the codec
/// stands on its own and can be reused (e.g. for an inline cache key).</para>
/// </summary>
public static class CompiledModuleCodec
{
    /// <summary>Magic prefix marking the bytes as a Shumway compiled-module
    /// blob. Lets a downstream reader fail fast on accidental wrong-data
    /// input rather than crashing mid-parse.</summary>
    public static readonly byte[] Magic = new byte[] { (byte)'S', (byte)'M', (byte)'C', (byte)'M' };
    /// <summary>v2 adds per-clause source positions for stack traces (chunk
    /// 55). The wire format gains a trailing block per predicate carrying
    /// each clause's <see cref="Shumway.Compiler.Lexer.SourcePosition"/>.
    /// It also carries, per predicate, the ADR-035 debug side tables (stop
    /// sites + per-clause frames/variables/head-args) — empty for a release
    /// predicate, populated for one compiled under <c>compile_mode=debug</c>,
    /// so a debug bundle is debuggable with no re-consult. The version number
    /// is not bumped for additive pre-release format changes.</summary>
    public const int CurrentVersion = 2;

    public static byte[] Encode(CompiledModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(Magic);
        bw.Write((uint)CurrentVersion);

        WriteStringTable(bw, module.StringLiterals);
        WriteFloatTable(bw, module.FloatLiterals);
        WriteBigIntTable(bw, module.BigIntLiterals);

        bw.Write((uint)module.Predicates.Count);
        foreach (var pred in module.Predicates)
            WritePredicate(bw, pred);

        bw.Flush();
        return ms.ToArray();
    }

    public static CompiledModule Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        byte[] magic = br.ReadBytes(4);
        if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException(
                "CompiledModuleCodec: magic bytes don't match 'SMCM'.");

        uint version = br.ReadUInt32();
        if (version != CurrentVersion)
            throw new InvalidDataException(
                $"CompiledModuleCodec: format version {version} not supported "
                + $"(expected {CurrentVersion}).");

        var stringLiterals = ReadStringTable(br);
        var floatLiterals = ReadFloatTable(br);
        var bigIntLiterals = ReadBigIntTable(br);

        uint predCount = br.ReadUInt32();
        var predicates = new List<CompiledPredicate>((int)predCount);
        for (uint i = 0; i < predCount; i++)
            predicates.Add(ReadPredicate(br));

        return new CompiledModule(predicates, stringLiterals, floatLiterals, bigIntLiterals);
    }

    // ---------- Predicate ----------

    private static void WritePredicate(BinaryWriter bw, CompiledPredicate pred)
    {
        var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
        string name = AtomTable.GetById(atomId)?.Name
                      ?? throw new InvalidOperationException(
                             $"Predicate's functor atom id {atomId} has no name.");
        WriteString(bw, name);
        bw.Write(pred.Arity);
        bw.Write(pred.ClauseCount);

        // Rewrite operand ids (atom / functor / builtin) into a stable
        // abstract form keyed off the per-predicate name tables. The reader
        // remaps the abstract ids back into the current process's tables.
        var (rewrittenBytes, atomNames, functorRefs, builtinRefs) =
            AbstractifyBytecode(pred.Bytecode);

        WriteStringList(bw, atomNames);
        bw.Write(functorRefs.Count);
        foreach (var (n, a) in functorRefs) { WriteString(bw, n); bw.Write(a); }
        bw.Write(builtinRefs.Count);
        foreach (var (n, a) in builtinRefs) { WriteString(bw, n); bw.Write(a); }

        bw.Write(rewrittenBytes.Length);
        bw.Write(rewrittenBytes);

        bw.Write(pred.CallSites.Count);
        foreach (var cs in pred.CallSites)
        {
            var (csAtomId, csArity) = FunctorTable.Lookup(cs.CalleeFunctorId);
            string csName = AtomTable.GetById(csAtomId)?.Name
                            ?? throw new InvalidOperationException(
                                   $"CallSite callee atom id {csAtomId} has no name.");
            bw.Write(cs.OpcodeOffset);
            WriteString(bw, csName);
            bw.Write(csArity);
            bw.Write(cs.IsExecute);
        }

        bw.Write(pred.DispatchSites.Count);
        foreach (int o in pred.DispatchSites) bw.Write(o);

        bw.Write(pred.SwitchTableIdSites.Count);
        foreach (int o in pred.SwitchTableIdSites) bw.Write(o);

        // For each switch table, infer its kind by inspecting the opcode at
        // the dispatching site. Phase-1 indexing emits at most one switch
        // site per table, so the first matching site's opcode is canonical.
        var switchKinds = InferSwitchTableKinds(pred);
        bw.Write(pred.SwitchTables.Count);
        for (int i = 0; i < pred.SwitchTables.Count; i++)
            WriteSwitchTable(bw, pred.SwitchTables[i], switchKinds[i]);

        // Predicate-level source position + per-clause positions.
        bw.Write(pred.SourcePosition.Line);
        bw.Write(pred.SourcePosition.Column);
        bw.Write(pred.SourcePosition.Offset);
        bw.Write(pred.ClauseSourcePositions.Count);
        foreach (var p in pred.ClauseSourcePositions)
        {
            bw.Write(p.Line);
            bw.Write(p.Column);
            bw.Write(p.Offset);
        }

        WriteDebugInfo(bw, pred);
    }

    /// <summary>ADR-035 — the debug side tables, empty for release predicates. A stop's
    /// <see cref="DebugStop.SiteId"/> is a GLOBAL <see cref="DebugSiteTable"/> id, valid
    /// only in the process that interned it, so we serialize the RESOLVED
    /// <c>(file, line, column)</c> and re-intern at decode — the same name-relative trick
    /// the atom/functor operands use. A module is one source file, so a single file name
    /// covers all of a predicate's stops.</summary>
    private static void WriteDebugInfo(BinaryWriter bw, CompiledPredicate pred)
    {
        bw.Write(pred.DebugStops.Count);
        if (pred.DebugStops.Count > 0)
        {
            var firstSite = DebugSiteTable.Get(pred.DebugStops[0].SiteId);
            WriteString(bw, DebugSiteTable.FileName(firstSite.FileId));
            foreach (var s in pred.DebugStops)
            {
                var site = DebugSiteTable.Get(s.SiteId);
                bw.Write(s.Offset);
                bw.Write(site.Line);
                bw.Write(site.Column);
            }
        }

        bw.Write(pred.DebugFrames.Count);
        foreach (var f in pred.DebugFrames)
        {
            bw.Write(f.Start);
            bw.Write(f.End);
            bw.Write(f.HasFrame);
            bw.Write(f.ClauseNumber);
            bw.Write(f.Variables.Count);
            foreach (var v in f.Variables) { WriteString(bw, v.Name); bw.Write(v.Slot); }
            if (f.HeadArgs is null) bw.Write(false);
            else
            {
                bw.Write(true);
                bw.Write(f.HeadArgs.Count);
                foreach (var t in f.HeadArgs) TermCodec.WriteTerm(bw, t);
            }
        }
    }

    private static CompiledPredicate ReadPredicate(BinaryReader br)
    {
        string name = ReadString(br);
        int arity = br.ReadInt32();
        int clauseCount = br.ReadInt32();

        var atomNames = ReadStringList(br);
        int functorRefCount = br.ReadInt32();
        var functorRefs = new List<(string, int)>(functorRefCount);
        for (int i = 0; i < functorRefCount; i++)
            functorRefs.Add((ReadString(br), br.ReadInt32()));
        int builtinRefCount = br.ReadInt32();
        var builtinRefs = new List<(string, int)>(builtinRefCount);
        for (int i = 0; i < builtinRefCount; i++)
            builtinRefs.Add((ReadString(br), br.ReadInt32()));

        int bytecodeLen = br.ReadInt32();
        byte[] abstractBytes = br.ReadBytes(bytecodeLen);

        byte[] bytecode = RehydrateBytecode(abstractBytes, atomNames, functorRefs, builtinRefs);

        int callSiteCount = br.ReadInt32();
        var callSites = new List<CallSite>(callSiteCount);
        for (int i = 0; i < callSiteCount; i++)
        {
            int offset = br.ReadInt32();
            string csName = ReadString(br);
            int csArity = br.ReadInt32();
            bool isExecute = br.ReadBoolean();
            int csFid = FunctorTable.Intern(
                AtomTable.Intern(csName, permanent: true).Id, csArity);
            callSites.Add(new CallSite(offset, csFid, isExecute));
        }

        int dispatchSiteCount = br.ReadInt32();
        var dispatchSites = new int[dispatchSiteCount];
        for (int i = 0; i < dispatchSiteCount; i++) dispatchSites[i] = br.ReadInt32();

        int switchTableIdSiteCount = br.ReadInt32();
        var switchTableIdSites = new int[switchTableIdSiteCount];
        for (int i = 0; i < switchTableIdSiteCount; i++) switchTableIdSites[i] = br.ReadInt32();

        int switchTableCount = br.ReadInt32();
        var switchTables = new SwitchTable[switchTableCount];
        for (int i = 0; i < switchTableCount; i++) switchTables[i] = ReadSwitchTable(br);

        // v2: predicate-level source position + per-clause positions.
        int predLine = br.ReadInt32();
        int predColumn = br.ReadInt32();
        int predOffset = br.ReadInt32();
        var predicatePosition = new Shumway.Compiler.Lexer.SourcePosition(
            predLine, predColumn, predOffset);

        int clausePosCount = br.ReadInt32();
        var clausePositions = new Shumway.Compiler.Lexer.SourcePosition[clausePosCount];
        for (int i = 0; i < clausePosCount; i++)
        {
            int line = br.ReadInt32();
            int column = br.ReadInt32();
            int offset = br.ReadInt32();
            clausePositions[i] = new Shumway.Compiler.Lexer.SourcePosition(line, column, offset);
        }

        var (debugStops, debugFrames) = ReadDebugInfo(br);

        int functorId = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, arity);
        var pred = new CompiledPredicate(
            bytecode, functorId, arity, clauseCount,
            callSites, dispatchSites, switchTables, switchTableIdSites,
            predicatePosition, clausePositions);
        pred.DebugStops = debugStops;
        pred.DebugFrames = debugFrames;
        return pred;
    }

    /// <summary>ADR-035 — read the debug side tables, re-interning each stop's
    /// <c>(file, line, column)</c> into THIS process's <see cref="DebugSiteTable"/> so its
    /// ids are valid here and its lines are registered for breakpoint binding. Returns
    /// empty arrays for a release predicate.</summary>
    private static (IReadOnlyList<DebugStop> Stops, IReadOnlyList<DebugClauseFrame> Frames)
        ReadDebugInfo(BinaryReader br)
    {
        int stopCount = br.ReadInt32();
        DebugStop[] stops = System.Array.Empty<DebugStop>();
        if (stopCount > 0)
        {
            string file = ReadString(br);
            int fileId = DebugSiteTable.InternFile(file);
            stops = new DebugStop[stopCount];
            for (int i = 0; i < stopCount; i++)
            {
                int off = br.ReadInt32();
                int line = br.ReadInt32();
                int col = br.ReadInt32();
                int siteId = DebugSiteTable.Intern(fileId, line, col);
                stops[i] = new DebugStop(off, siteId);
            }
        }

        int frameCount = br.ReadInt32();
        var frames = new DebugClauseFrame[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            int start = br.ReadInt32();
            int end = br.ReadInt32();
            bool hasFrame = br.ReadBoolean();
            int clauseNumber = br.ReadInt32();
            int varCount = br.ReadInt32();
            var vars = new DebugVariable[varCount];
            for (int j = 0; j < varCount; j++)
            {
                string vname = ReadString(br);
                int slot = br.ReadInt32();
                vars[j] = new DebugVariable(vname, slot);
            }
            IReadOnlyList<Shumway.Compiler.Ast.Term>? headArgs = null;
            if (br.ReadBoolean())
            {
                int haCount = br.ReadInt32();
                var ha = new Shumway.Compiler.Ast.Term[haCount];
                for (int j = 0; j < haCount; j++) ha[j] = TermCodec.ReadTerm(br);
                headArgs = ha;
            }
            frames[i] = new DebugClauseFrame(start, end, hasFrame, vars)
            {
                HeadArgs = headArgs,
                ClauseNumber = clauseNumber,
            };
        }
        return (stops, frames);
    }

    // ---------- Switch tables ----------

    private static SwitchTableKind[] InferSwitchTableKinds(CompiledPredicate pred)
    {
        var kinds = new SwitchTableKind[pred.SwitchTables.Count];
        // Default to Integer so a switch table with no recorded site (shouldn't
        // happen but plays defence) still survives the round-trip.
        for (int i = 0; i < kinds.Length; i++) kinds[i] = SwitchTableKind.Integer;

        foreach (int siteOffset in pred.SwitchTableIdSites)
        {
            // The operand site is the 4-byte table-id int. For the arg-0
            // opcodes (SwitchOnAtom etc.) the opcode byte is at site - 1
            // (table-id immediately follows opcode). For the multi-arg
            // variants (SwitchOnAtomArg etc.) the opcode byte is at
            // site - 5 (a 4-byte arg_idx operand sits between opcode and
            // table-id).
            int tableId = BytecodeIO.ReadInt32(pred.Bytecode, siteOffset);
            byte opcodeByteOld = pred.Bytecode[siteOffset - 1];
            SwitchTableKind? kind = (Opcode)opcodeByteOld switch
            {
                Opcode.SwitchOnAtom => SwitchTableKind.Atom,
                Opcode.SwitchOnInteger => SwitchTableKind.Integer,
                Opcode.SwitchOnStructure => SwitchTableKind.Structure,
                _ => null,
            };
            if (kind is null && siteOffset >= 5)
            {
                byte opcodeByteArg = pred.Bytecode[siteOffset - 5];
                kind = (Opcode)opcodeByteArg switch
                {
                    Opcode.SwitchOnAtomArg => SwitchTableKind.Atom,
                    Opcode.SwitchOnIntegerArg => SwitchTableKind.Integer,
                    Opcode.SwitchOnStructureArg => SwitchTableKind.Structure,
                    _ => null,
                };
            }
            if (kind is null && siteOffset >= 13)
            {
                // ADR-027 sub-switches: 17-byte encoding, table-id at
                // opcode+13 (argIdx + sub0 + sub1 sit in between). Without
                // this case a sub-switch table fell to the Integer default,
                // so an ATOM-keyed table's compile-process atom ids were
                // serialized raw and reloaded verbatim in a fresh process —
                // every lookup missed and dispatch ran the default chain
                // (Blint --exe: tokenizer loop / mass parse failures).
                byte opcodeByteSub = pred.Bytecode[siteOffset - 13];
                kind = (Opcode)opcodeByteSub switch
                {
                    Opcode.SwitchOnAtomSub => SwitchTableKind.Atom,
                    Opcode.SwitchOnIntegerSub => SwitchTableKind.Integer,
                    _ => null,
                };
            }
            if (kind is not null) kinds[tableId] = kind.Value;
        }
        return kinds;
    }

    private enum SwitchTableKind : byte { Integer = 0, Atom = 1, Structure = 2 }

    private static void WriteSwitchTable(BinaryWriter bw, SwitchTable table, SwitchTableKind kind)
    {
        bw.Write((byte)kind);
        bw.Write(table.DefaultAddress);
        bw.Write(table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            int key = table.Keys[i];
            int value = table.Values[i];
            switch (kind)
            {
                case SwitchTableKind.Integer:
                    bw.Write(key);
                    break;
                case SwitchTableKind.Atom:
                    WriteString(bw, AtomTable.GetById(key)?.Name
                                     ?? throw new InvalidOperationException(
                                            $"Atom id {key} in switch table has no name."));
                    break;
                case SwitchTableKind.Structure:
                {
                    var (atomId, ar) = FunctorTable.Lookup(key);
                    WriteString(bw, AtomTable.GetById(atomId)?.Name
                                     ?? throw new InvalidOperationException(
                                            $"Functor atom id {atomId} in switch table has no name."));
                    bw.Write(ar);
                    break;
                }
            }
            bw.Write(value);
        }
    }

    private static SwitchTable ReadSwitchTable(BinaryReader br)
    {
        var kind = (SwitchTableKind)br.ReadByte();
        int defaultAddr = br.ReadInt32();
        int count = br.ReadInt32();
        int[] keys = new int[count];
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
        {
            switch (kind)
            {
                case SwitchTableKind.Integer:
                    keys[i] = br.ReadInt32();
                    break;
                case SwitchTableKind.Atom:
                {
                    string name = ReadString(br);
                    keys[i] = AtomTable.Intern(name, permanent: true).Id;
                    break;
                }
                case SwitchTableKind.Structure:
                {
                    string name = ReadString(br);
                    int ar = br.ReadInt32();
                    keys[i] = FunctorTable.Intern(
                        AtomTable.Intern(name, permanent: true).Id, ar);
                    break;
                }
            }
            values[i] = br.ReadInt32();
        }
        return new SwitchTable(keys, values, defaultAddr);
    }

    // ---------- Bytecode operand abstraction ----------

    /// <summary>Walks the bytecode and replaces every Atom / Functor / BuiltinId
    /// operand with its index into a per-predicate name table — yielding bytes
    /// that are independent of the current process's interner state. The
    /// returned tables let the loader rehydrate the original ids in its own
    /// table state.</summary>
    private static (byte[] AbstractBytes, List<string> AtomNames,
                    List<(string Name, int Arity)> FunctorRefs,
                    List<(string Name, int Arity)> BuiltinRefs)
        AbstractifyBytecode(byte[] bytecode)
    {
        var atomNames = new List<string>();
        var atomNameIndex = new Dictionary<int, int>();
        var functorRefs = new List<(string, int)>();
        var functorRefIndex = new Dictionary<int, int>();
        var builtinRefs = new List<(string, int)>();
        var builtinRefIndex = new Dictionary<int, int>();

        byte[] outBytes = (byte[])bytecode.Clone();
        int pc = 0;
        while (pc < bytecode.Length)
        {
            byte opByte = bytecode[pc];
            var info = OpcodeTable.Get(opByte);
            if (!info.IsDefined)
                throw new InvalidDataException(
                    $"AbstractifyBytecode: opcode 0x{opByte:X2} at offset {pc} not defined.");

            int operandOffset = pc + 1;
            if (info.OperandKinds is not null)
            {
                for (int i = 0; i < info.OperandKinds.Length; i++)
                {
                    int operandValue = BytecodeIO.ReadInt32(bytecode, operandOffset);
                    int abstractValue = info.OperandKinds[i] switch
                    {
                        OperandKind.Atom => InternIndex(operandValue,
                            id => AtomTable.GetById(id)?.Name
                                  ?? throw new InvalidOperationException(
                                         $"Atom id {id} has no name."),
                            atomNames, atomNameIndex),
                        OperandKind.Functor => InternFunctorIndex(operandValue,
                            functorRefs, functorRefIndex),
                        OperandKind.BuiltinId => InternBuiltinIndex(operandValue,
                            builtinRefs, builtinRefIndex),
                        _ => operandValue,
                    };
                    BytecodeIO.WriteInt32(outBytes, operandOffset, abstractValue);
                    operandOffset += 4;
                }
            }
            pc += info.Size;
        }
        return (outBytes, atomNames, functorRefs, builtinRefs);
    }

    private static byte[] RehydrateBytecode(
        byte[] abstractBytes,
        IReadOnlyList<string> atomNames,
        IReadOnlyList<(string Name, int Arity)> functorRefs,
        IReadOnlyList<(string Name, int Arity)> builtinRefs)
    {
        byte[] outBytes = (byte[])abstractBytes.Clone();
        int pc = 0;
        while (pc < abstractBytes.Length)
        {
            byte opByte = abstractBytes[pc];
            var info = OpcodeTable.Get(opByte);
            if (!info.IsDefined)
                throw new InvalidDataException(
                    $"RehydrateBytecode: opcode 0x{opByte:X2} at offset {pc} not defined.");

            int operandOffset = pc + 1;
            if (info.OperandKinds is not null)
            {
                for (int i = 0; i < info.OperandKinds.Length; i++)
                {
                    int idx = BytecodeIO.ReadInt32(abstractBytes, operandOffset);
                    int concreteValue = info.OperandKinds[i] switch
                    {
                        OperandKind.Atom =>
                            AtomTable.Intern(atomNames[idx], permanent: true).Id,
                        OperandKind.Functor =>
                            FunctorTable.Intern(
                                AtomTable.Intern(functorRefs[idx].Name, permanent: true).Id,
                                functorRefs[idx].Arity),
                        OperandKind.BuiltinId =>
                            ResolveBuiltinId(builtinRefs[idx].Name, builtinRefs[idx].Arity),
                        _ => idx,
                    };
                    BytecodeIO.WriteInt32(outBytes, operandOffset, concreteValue);
                    operandOffset += 4;
                }
            }
            pc += info.Size;
        }
        return outBytes;
    }

    private static int ResolveBuiltinId(string name, int arity)
    {
        int functorId = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, arity);
        if (!BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
            throw new InvalidDataException(
                $"Bundle references builtin {name}/{arity}, but it isn't registered "
                + "in this process. (Did StandardBuiltins.EnsureRegistered run yet?)");
        return builtinId;
    }

    private static int InternIndex(
        int operandValue,
        Func<int, string> nameOf,
        List<string> table,
        Dictionary<int, int> index)
    {
        if (index.TryGetValue(operandValue, out int existing)) return existing;
        int slot = table.Count;
        table.Add(nameOf(operandValue));
        index[operandValue] = slot;
        return slot;
    }

    private static int InternFunctorIndex(
        int functorId,
        List<(string, int)> table,
        Dictionary<int, int> index)
    {
        if (index.TryGetValue(functorId, out int existing)) return existing;
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        string name = AtomTable.GetById(atomId)?.Name
                      ?? throw new InvalidOperationException(
                             $"Functor's atom id {atomId} has no name.");
        int slot = table.Count;
        table.Add((name, arity));
        index[functorId] = slot;
        return slot;
    }

    private static int InternBuiltinIndex(
        int builtinId,
        List<(string, int)> table,
        Dictionary<int, int> index)
    {
        if (index.TryGetValue(builtinId, out int existing)) return existing;
        var entry = BuiltinsRegistry.GetById(builtinId);
        int slot = table.Count;
        table.Add((entry.Name, entry.Arity));
        index[builtinId] = slot;
        return slot;
    }

    // ---------- Primitive table writers ----------

    private static void WriteStringTable(BinaryWriter bw, IReadOnlyList<string> strings)
    {
        bw.Write(strings.Count);
        foreach (string s in strings) WriteString(bw, s);
    }

    private static List<string> ReadStringTable(BinaryReader br)
    {
        int count = br.ReadInt32();
        var list = new List<string>(count);
        for (int i = 0; i < count; i++) list.Add(ReadString(br));
        return list;
    }

    private static void WriteFloatTable(BinaryWriter bw, IReadOnlyList<double> floats)
    {
        bw.Write(floats.Count);
        foreach (double d in floats) bw.Write(d);
    }

    private static List<double> ReadFloatTable(BinaryReader br)
    {
        int count = br.ReadInt32();
        var list = new List<double>(count);
        for (int i = 0; i < count; i++) list.Add(br.ReadDouble());
        return list;
    }

    private static void WriteBigIntTable(BinaryWriter bw, IReadOnlyList<System.Numerics.BigInteger> bigs)
    {
        bw.Write(bigs.Count);
        foreach (var v in bigs)
        {
            byte[] bytes = v.ToByteArray();
            bw.Write(bytes.Length);
            bw.Write(bytes);
        }
    }

    private static List<System.Numerics.BigInteger> ReadBigIntTable(BinaryReader br)
    {
        int count = br.ReadInt32();
        var list = new List<System.Numerics.BigInteger>(count);
        for (int i = 0; i < count; i++)
        {
            int len = br.ReadInt32();
            byte[] bytes = br.ReadBytes(len);
            if (bytes.Length != len)
                throw new InvalidDataException(
                    $"CompiledModuleCodec: truncated BigInteger blob (expected {len} bytes, got {bytes.Length}).");
            list.Add(new System.Numerics.BigInteger(bytes));
        }
        return list;
    }

    private static void WriteStringList(BinaryWriter bw, IReadOnlyList<string> strings)
    {
        bw.Write(strings.Count);
        foreach (string s in strings) WriteString(bw, s);
    }

    private static List<string> ReadStringList(BinaryReader br)
    {
        int count = br.ReadInt32();
        var list = new List<string>(count);
        for (int i = 0; i < count; i++) list.Add(ReadString(br));
        return list;
    }

    private static void WriteString(BinaryWriter bw, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadString(BinaryReader br)
    {
        int length = br.ReadInt32();
        byte[] bytes = br.ReadBytes(length);
        if (bytes.Length != length)
            throw new InvalidDataException(
                $"CompiledModuleCodec: truncated string (expected {length} bytes, got {bytes.Length}).");
        return Encoding.UTF8.GetString(bytes);
    }
}
