using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    /// <summary><c>stamp_date_time(+Stamp, -DateTime, +TimeZone)</c> —
    /// converts a Unix-epoch stamp (float seconds) into the SWI
    /// <c>date(Y, M, D, H, Mi, S, Off, TZ, DST)</c> compound. The
    /// TimeZone arg is honoured for the atoms <c>'UTC'</c> and
    /// <c>local</c>; any other atom is treated as the local zone
    /// (full IANA-name lookup isn't worth the System.TimeZoneInfo
    /// wiring for the typical caller).</summary>
    public static bool StampDateTime(Activation engine)
    {
        Cell stampCell = ResolveLocal(engine, engine.GetRegister(0));
        Cell tzCell = ResolveLocal(engine, engine.GetRegister(2));
        if (stampCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        double stamp = stampCell.Tag switch
        {
            Tag.Float => Cell.DecodeFloat(stampCell, engine.GetHeap(stampCell.FloatPairedIndex)),
            Tag.Int => stampCell.AsInt,
            _ => throw new PrologRuntimeException("type_error", "number"),
        };
        string tzName = tzCell.Tag == Tag.Atom
            ? (AtomTable.GetById(tzCell.AsAtomId)?.Name ?? "local")
            : "local";

        DateTime utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddSeconds(stamp);
        DateTime local = string.Equals(tzName, "UTC", StringComparison.OrdinalIgnoreCase)
            ? utc
            : utc.ToLocalTime();
        TimeSpan offset = string.Equals(tzName, "UTC", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.Zero
            : TimeZoneInfo.Local.GetUtcOffset(utc);

        var dt = new CompoundTerm("date", new Term[]
        {
            new IntTerm(local.Year),
            new IntTerm(local.Month),
            new IntTerm(local.Day),
            new IntTerm(local.Hour),
            new IntTerm(local.Minute),
            new FloatTerm(local.Second + local.Millisecond / 1000.0),
            new IntTerm((long)offset.TotalSeconds),
            new AtomTerm(tzName),
            new AtomTerm("-"),  // DST flag — '-' = unknown/n-a.
        });
        Cell dtCell = Materializer.MaterializeAsCell(engine, dt);
        return engine.UnifyRegisterWithCell(1, dtCell);
    }

    // ============================================================================
    // functor/3, arg/3, =../2
    // ============================================================================

    /// <summary><c>functor(Term, Name, Arity)</c> — bidirectional term
    /// introspection. With <c>Term</c> bound, decomposes into its functor
    /// name and arity (atomic terms are name = themselves, arity = 0).
    /// With <c>Term</c> unbound and <c>Name</c> + <c>Arity</c> ground,
    /// builds a fresh compound with <c>Arity</c> anonymous unbound
    /// arguments.</summary>
    public static bool Functor(Activation engine)
    {
        // A packed list is a list, so it must answer '.'/2 like the cons cell
        // it denotes (ADR-047). Materialising the pair once here is what lets
        // the arms below read a compound's parts out of heap slots.
        Cell t = engine.MaterializeListCell(ResolveLocal(engine, engine.GetRegister(0)));

        if (t.Tag is Tag.Atom or Tag.Int or Tag.BigInt or Tag.Rational or Tag.Float)
        {
            if (!engine.UnifyRegisterWithCell(1, t)) return false;
            return engine.UnifyRegisterWithCell(2, Cell.Int(0));
        }
        if (t.Tag == Tag.Str)
        {
            int functorIdx = t.AsHeapIndex;
            var (atomId, arity) = FunctorTable.Lookup(
                engine.GetHeap(functorIdx).AsFunctorId);
            if (!engine.UnifyRegisterWithCell(1, Cell.Atom(atomId))) return false;
            return engine.UnifyRegisterWithCell(2, Cell.Int(arity));
        }
        if (t.Tag == Tag.Lis)
        {
            int dotId = AtomTable.Intern(".", permanent: true).Id;
            if (!engine.UnifyRegisterWithCell(1, Cell.Atom(dotId))) return false;
            return engine.UnifyRegisterWithCell(2, Cell.Int(2));
        }
        if (t.Tag == Tag.Ref)
        {
            // Construct mode (§8.5.1.3): Name and Arity must be ground,
            // Arity a non-negative integer the address space can hold, Name an
            // atom unless Arity is 0. Every error carries its culprit.
            Cell n = ResolveLocal(engine, engine.GetRegister(1));
            Cell a = ResolveLocal(engine, engine.GetRegister(2));
            if (n.Tag is Tag.Ref or Tag.AttVar || a.Tag is Tag.Ref or Tag.AttVar)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            Term nameTerm = MaterializeRegister(engine, 1);
            Term arityTerm = MaterializeRegister(engine, 2);
            if (a.Tag == Tag.BigInt)
            {
                // A bignum IS an integer: negative is a domain error;
                // positive is past what the address space can represent —
                // with max_arity unbounded that is a RESOURCE answer
                // (issue #106), not a flag-derived representation error.
                throw new ShumwayPrologException(
                    engine.AsBigInt(a).Sign < 0
                        ? IsoError.DomainError("not_less_than_zero", arityTerm)
                        : IsoError.ResourceError("finite_memory"));
            }
            if (a.Tag != Tag.Int)
                throw new ShumwayPrologException(
                    IsoError.TypeError("integer", arityTerm));
            long arity = a.AsInt;
            if (arity < 0)
                throw new ShumwayPrologException(
                    IsoError.DomainError("not_less_than_zero", arityTerm));
            // Checked BEFORE any allocation: the probe one past capacity
            // must error, not thrash 4 GiB of heap into being (issue #106).
            if (arity > MaxArity)
                throw new ShumwayPrologException(
                    IsoError.ResourceError("finite_memory"));
            if (arity == 0)
            {
                // T becomes Name itself (atomic).
                if (n.Tag is not (Tag.Atom or Tag.Int or Tag.BigInt or Tag.Rational or Tag.Float))
                    throw new ShumwayPrologException(
                        IsoError.TypeError("atomic", nameTerm));
                return engine.UnifyRegisterWithCell(0, n);
            }
            if (n.Tag is Tag.Int or Tag.BigInt or Tag.Rational or Tag.Float)
                throw new ShumwayPrologException(IsoError.TypeError("atom", nameTerm));
            if (n.Tag != Tag.Atom)
                throw new ShumwayPrologException(IsoError.TypeError("atomic", nameTerm));
            // '.'/2 IS the list constructor, and a cons lives in a Lis cell
            // everywhere else in the engine (ADR-017). Building a Str here
            // spelled a list that compared unequal to the list it spells:
            // functor(T, '.', 2) has to give a partial list.
            if (arity == 2 && n.AsAtomId == DotAtomId)
            {
                int consSlot = engine.AllocateHeap(2);
                engine.SetHeap(consSlot,     Cell.UnboundVar(consSlot));
                engine.SetHeap(consSlot + 1, Cell.UnboundVar(consSlot + 1));
                return engine.UnifyRegisterWithCell(0, Cell.Lis(consSlot));
            }
            int functorId = FunctorTable.Intern(n.AsAtomId, (int)arity);
            int strBase = engine.AllocateHeap(2 + (int)arity);
            engine.SetHeap(strBase, Cell.Str(strBase + 1));
            engine.SetHeap(strBase + 1, Cell.Functor(functorId));
            for (int i = 0; i < arity; i++)
            {
                int slot = strBase + 2 + i;
                engine.SetHeap(slot, Cell.UnboundVar(slot));
            }
            return engine.UnifyRegisterWithCell(0, Cell.Ref(strBase));
        }
        return false;
    }

    /// <summary><c>compound_name_arity(?Compound, ?Name, ?Arity)</c> (SWI) —
    /// like <c>functor/3</c> but restricted to compound terms (arity ≥ 1). A
    /// bound compound decomposes into its name and arity; a bound non-compound
    /// (an atomic) is a <c>type_error(compound, _)</c>. With <c>Compound</c>
    /// unbound, <c>Name</c> (an atom) and <c>Arity</c> (an integer ≥ 1) construct
    /// a fresh compound with unbound arguments.</summary>
    public static bool CompoundNameArity(Activation engine)
    {
        Cell t = engine.MaterializeListCell(ResolveLocal(engine, engine.GetRegister(0)));
        if (t.Tag == Tag.Str)
        {
            int functorIdx = t.AsHeapIndex;
            var (atomId, arity) = FunctorTable.Lookup(engine.GetHeap(functorIdx).AsFunctorId);
            if (!engine.UnifyRegisterWithCell(1, Cell.Atom(atomId))) return false;
            return engine.UnifyRegisterWithCell(2, Cell.Int(arity));
        }
        if (t.Tag == Tag.Lis)
        {
            int dotId = AtomTable.Intern(".", permanent: true).Id;
            if (!engine.UnifyRegisterWithCell(1, Cell.Atom(dotId))) return false;
            return engine.UnifyRegisterWithCell(2, Cell.Int(2));
        }
        if (t.Tag == Tag.Ref)
        {
            Cell n = ResolveLocal(engine, engine.GetRegister(1));
            Cell a = ResolveLocal(engine, engine.GetRegister(2));
            if (n.Tag == Tag.Ref || a.Tag == Tag.Ref)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            if (a.Tag != Tag.Int)
                throw new ShumwayPrologException(IsoError.TypeError("integer", new VarTerm("_")));
            long arity = a.AsInt;
            // A compound has arity ≥ 1; arity 0 would be atomic, not a compound.
            if (arity < 1)
                throw new ShumwayPrologException(IsoError.TypeError("compound", new VarTerm("_")));
            if (n.Tag != Tag.Atom)
                throw new ShumwayPrologException(IsoError.TypeError("atom", new VarTerm("_")));
            int functorId = FunctorTable.Intern(n.AsAtomId, (int)arity);
            int strBase = engine.AllocateHeap(2 + (int)arity);
            engine.SetHeap(strBase, Cell.Str(strBase + 1));
            engine.SetHeap(strBase + 1, Cell.Functor(functorId));
            for (int i = 0; i < arity; i++)
            {
                int slot = strBase + 2 + i;
                engine.SetHeap(slot, Cell.UnboundVar(slot));
            }
            return engine.UnifyRegisterWithCell(0, Cell.Ref(strBase));
        }
        // Bound to an atomic — not a compound.
        throw new ShumwayPrologException(IsoError.TypeError("compound", new VarTerm("_")));
    }

    /// <summary><c>arg(N, Term, Arg)</c> — the N-th argument (1-indexed)
    /// of a compound term. Fails when N is out of range or <c>Term</c>
    /// isn't a compound.</summary>
    public static bool Arg(Activation engine)
    {
        Cell nCell = ResolveLocal(engine, engine.GetRegister(0));
        Cell tCell = engine.MaterializeListCell(ResolveLocal(engine, engine.GetRegister(1)));
        // §8.5.2.3 order: the TERM is checked first — an unbound term is
        // an instantiation_error and a non-compound is
        // type_error(compound, T), whatever N looks like.
        if (tCell.Tag is Tag.Ref or Tag.AttVar)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (tCell.Tag is not (Tag.Str or Tag.Lis))
            throw new ShumwayPrologException(
                IsoError.TypeError("compound", MaterializeRegister(engine, 1)));
        if (nCell.Tag is Tag.Ref or Tag.AttVar)
        {
            // SWI's arg/3 ENUMERATES the arguments when N is unbound
            // (occurs.pl's contains_term walks subterms with arg(_, T, A));
            // ISO wants an instantiation_error there. Gated on the caller
            // living in an SWI-dialect module.
            if (engine.Host is Shumway.Builtins.IDialectAwareHost dh0
                && dh0.CallerModuleHasDialect(engine, "swi"))
                return ArgEnumerate(engine, tCell);
            throw new ShumwayPrologException(IsoError.InstantiationError());
        }
        if (nCell.Tag == Tag.Int && nCell.AsInt < 0)
            throw new ShumwayPrologException(IsoError.DomainError(
                "not_less_than_zero", MaterializeRegister(engine, 0)));
        if (nCell.Tag == Tag.BigInt)
        {
            // A bignum index IS an integer: negative is still a domain
            // error, positive is simply past every argument.
            if (engine.AsBigInt(nCell).Sign < 0)
                throw new ShumwayPrologException(IsoError.DomainError(
                    "not_less_than_zero", MaterializeRegister(engine, 0)));
            return false;
        }
        if (nCell.Tag != Tag.Int)
        {
            // SWI's arg/3 ENUMERATES the arguments when N is unbound
            // (occurs.pl's contains_term walks subterms with arg(_, T, A));
            // ISO wants an error there. Gated on the caller living in an
            // SWI-dialect module, so strict programs keep ISO behaviour.
            throw new ShumwayPrologException(
                IsoError.TypeError("integer", MaterializeRegister(engine, 0)));
        }
        long n = nCell.AsInt;

        if (tCell.Tag == Tag.Str)
        {
            int functorIdx = tCell.AsHeapIndex;
            var (_, arity) = FunctorTable.Lookup(
                engine.GetHeap(functorIdx).AsFunctorId);
            if (n < 1 || n > arity) return false;
            return engine.UnifyRegisterWithHeapAt(2, functorIdx + (int)n);
        }
        if (tCell.Tag == Tag.Lis)
        {
            // List has arity 2: arg(1) = head, arg(2) = tail.
            if (n < 1 || n > 2) return false;
            int headIdx = tCell.AsHeapIndex;
            return engine.UnifyRegisterWithHeapAt(2, headIdx + (int)(n - 1));
        }
        return false;
    }

    /// <summary><c>'$cp_owners'/0</c> — diagnostic: dumps every live choice
    /// point (stack slot, saved backtrack address, arity) with the nearest
    /// predicate at-or-below the address, to stderr. Attribution caveat: for
    /// in-place dynamic chain chunks the label is nearest-predicate-below,
    /// which can name the wrong neighbour.</summary>
    public static bool CpOwners(Activation engine)
    {
        // Address→functor from the LIVE map (covers live-linked predicates the
        // per-query PredicatesByAddress snapshot cannot see).
        var byAddr = new System.Collections.Generic.SortedDictionary<int, int>();
        if (engine.CurrentFunctorAddresses is { } famap)
            foreach (var kv in famap)
                if (kv.Value >= 0 && !Activation.IsResumeMarker(kv.Value))
                    byAddr[kv.Value] = kv.Key;
        var addrs = new int[byAddr.Count];
        byAddr.Keys.CopyTo(addrs, 0);
        string NameAt(int bp)
        {
            if (addrs.Length == 0) return "?";
            int lo = 0, hi = addrs.Length - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (addrs[mid] <= bp) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (best < 0) return "?";
            var (atomId, arity) = FunctorTable.Lookup(byAddr[addrs[best]]);
            return $"{AtomTable.GetById(atomId)?.Name}/{arity}@+{bp - addrs[best]}";
        }
        foreach (var (b, bp, arity) in engine.EnumerateChoicePoints())
        {
            int savedCp = engine.CpSavedContinuation(b);
            Console.Error.WriteLine(
                $"[CP] b={b} bp={bp} arity={arity} owner={NameAt(bp)} caller={NameAt(savedCp)}");
        }
        return true;
    }

    // arg(N, Term, Arg) with N unbound, SWI mode: yield (1, arg1), (2, arg2), …
    // backtrackably. Arity 3 so the cursor CP restores all three registers.
    private static bool ArgEnumerate(Activation engine, Cell tCell)
    {
        int argBase, arity;
        if (tCell.Tag == Tag.Str)
        {
            int functorIdx = tCell.AsHeapIndex;
            (_, arity) = FunctorTable.Lookup(engine.GetHeap(functorIdx).AsFunctorId);
            argBase = functorIdx + 1;
        }
        else
        {
            argBase = tCell.AsHeapIndex;
            arity = 2;
        }
        int returnPc = engine.BuiltinReturnPc;
        return Shumway.Core.IndexEnumCursor.Start(engine, arity, 3, returnPc,
            (e, i) => e.UnifyRegisterWithCell(0, Cell.Int(i + 1))
                   && e.UnifyRegisterWithHeapAt(2, argBase + i));
    }

    /// <summary><c>T =.. List</c> — the "univ" operator. Decomposes a
    /// compound into <c>[Functor | Args]</c> (or yields <c>[Atom]</c>
    /// for atomic <c>T</c>), or composes <c>T</c> from such a list.</summary>
    // Cached atom ids — boyer hits =../2 in a tight loop; avoid the
    // AtomTable hash lookup per call. Permanent atoms get permanent
    // ids that never get reused, so caching is safe.
    private static int _dotAtomIdCache;
    private static int DotAtomId
    {
        get
        {
            if (_dotAtomIdCache == 0)
                _dotAtomIdCache = AtomTable.Intern(".", permanent: true).Id;
            return _dotAtomIdCache;
        }
    }

    /// <summary>The largest arity a compound term can be REPRESENTED with —
    /// address-space capacity, not the <c>max_arity</c> flag (which reports
    /// <c>unbounded</c>; hitting this capacity answers
    /// <c>resource_error(finite_memory)</c> — issue #106).
    ///
    /// <para>It is a size, not a taste. Nothing in the machine wants a small
    /// one: a heap reference is a 32-bit index, the functor table keeps the
    /// arity in an int, bytecode operands are ints, and the argument
    /// registers grow by doubling with no cap. What bounds a term is what a
    /// term COSTS, which is N+1 cells of eight bytes, against the address
    /// space the host has -- so the number comes from
    /// <see cref="RuntimeCaps.MaxArity"/> and is smaller in a browser.</para>
    ///
    /// <para>A wide term of variables is how one models an array, and a
    /// couple of hundred arguments is nowhere near the cost of anything. The
    /// old value was 255 with a comment blaming a uint16 register index that
    /// does not exist, and it was not even enforced: a 300-argument predicate
    /// consulted, compiled and ran, while functor/3 alone refused to build
    /// one.</para></summary>
    internal static int MaxArity => RuntimeCaps.MaxArity;

    public static bool Univ(Activation engine)
    {
        Cell t = engine.MaterializeListCell(ResolveLocal(engine, engine.GetRegister(0)));

        // Decompose modes — build the list directly in the heap with
        // a single allocation, no intermediate Cell[] buffer.
        if (t.Tag is Tag.Atom or Tag.Int or Tag.BigInt or Tag.Rational or Tag.Float)
        {
            // Single-element list: [t] = .(t, []).
            int idx = engine.AllocateHeap(3);
            engine.SetHeap(idx,     Cell.Lis(idx + 1));
            engine.SetHeap(idx + 1, t);
            engine.SetHeap(idx + 2, Cell.Atom(AtomTable.EmptyListId));
            return engine.UnifyRegisterWithHeapAt(1, idx);
        }
        if (t.Tag == Tag.Str)
        {
            int functorIdx = t.AsHeapIndex;
            var (atomId, arity) = FunctorTable.Lookup(
                engine.GetHeap(functorIdx).AsFunctorId);
            // Fast path: [Functor | Args] built directly. Layout:
            //   idx+0: Lis(idx+1)        -- first cons
            //   idx+1: Atom(functor)     -- head: the functor atom
            //   idx+2: Lis(idx+3)        -- next cons (arg 0)
            //   idx+3: <arg 0>           -- head: copied from STR
            //   ...
            //   idx+2k: Lis(idx+2k+1)    -- cons for arg k-1
            //   idx+2k+1: <arg k-1>
            //   idx+2(arity+1): Atom([]) -- terminating nil
            int total = 2 * (1 + arity) + 1;
            int idx = engine.AllocateHeap(total);
            engine.SetHeap(idx,     Cell.Lis(idx + 1));
            engine.SetHeap(idx + 1, Cell.Atom(atomId));
            for (int i = 0; i < arity; i++)
            {
                int cons = idx + 2 + 2 * i;
                engine.SetHeap(cons,     Cell.Lis(cons + 1));
                engine.SetHeap(cons + 1, engine.GetHeap(functorIdx + 1 + i));
            }
            engine.SetHeap(idx + 2 * (1 + arity), Cell.Atom(AtomTable.EmptyListId));
            return engine.UnifyRegisterWithHeapAt(1, idx);
        }
        if (t.Tag == Tag.Lis)
        {
            // Lis cell represents a [Head|Tail] cons — its =.. result
            // is the 3-element list ['.', Head, Tail].
            int headIdx = t.AsHeapIndex;
            int idx = engine.AllocateHeap(7);
            engine.SetHeap(idx,     Cell.Lis(idx + 1));
            engine.SetHeap(idx + 1, Cell.Atom(DotAtomId));
            engine.SetHeap(idx + 2, Cell.Lis(idx + 3));
            engine.SetHeap(idx + 3, engine.GetHeap(headIdx));
            engine.SetHeap(idx + 4, Cell.Lis(idx + 5));
            engine.SetHeap(idx + 5, engine.GetHeap(headIdx + 1));
            engine.SetHeap(idx + 6, Cell.Atom(AtomTable.EmptyListId));
            return engine.UnifyRegisterWithHeapAt(1, idx);
        }
        if (t.Tag == Tag.Ref)
        {
            // Compose: walk the list twice — once to count, once to
            // build the STR. The list is on the heap so the walk is a
            // pointer chase, no allocation.
            Cell listC = ResolveLocal(engine, engine.GetRegister(1));
            Term listTerm = MaterializeRegister(engine, 1);
            // §8.5.3.3 order: an UNBOUND list (or tail) is an
            // instantiation_error; an improper one is type_error(list, L)
            // with the whole list as culprit.
            if (listC.Tag is Tag.Ref or Tag.AttVar)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            int count = 0;
            Cell cur = engine.NormalizeListCell(listC);
            while (engine.TryUnconsListLike(cur, out _, out Cell tail))
            {
                count++;
                cur = engine.NormalizeListCell(ResolveLocal(engine, tail));
            }
            if (cur.Tag is Tag.Ref or Tag.AttVar)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
                throw new ShumwayPrologException(IsoError.TypeError("list", listTerm));
            if (count == 0)
                throw new ShumwayPrologException(
                    IsoError.DomainError("non_empty_list", listTerm));

            // Fetch the functor cell (the first element) — through the
            // list-like cursor: a packed list IS a list (ADR-047), and
            // reading AsHeapIndex as a cons head slot misreads a PSTR.
            engine.TryUnconsListLike(engine.NormalizeListCell(listC),
                out Cell firstRaw, out Cell restAfterFirst);
            Cell first = ResolveLocal(engine, firstRaw);
            Term firstTerm = engine.MaterializeCellToTerm is { } fmat
                && fmat(first) is Term ft ? ft : new VarTerm("_");
            if (first.Tag is Tag.Ref or Tag.AttVar)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            if (count == 1)
            {
                if (first.Tag is not (Tag.Atom or Tag.Int or Tag.BigInt or Tag.Rational or Tag.Float))
                    throw new ShumwayPrologException(
                        IsoError.TypeError("atomic", firstTerm));
                return engine.UnifyRegisterWithCell(0, first);
            }
            if (first.Tag != Tag.Atom)
                throw new ShumwayPrologException(IsoError.TypeError("atom", firstTerm));
            int arity = count - 1;
            // Past address-space capacity the term cannot be built; with
            // max_arity unbounded that is a resource error (issue #106).
            if (arity > MaxArity)
                throw new ShumwayPrologException(
                    IsoError.ResourceError("finite_memory"));
            // Same rule as functor/3 above: a '.'/2 term is a cons cell.
            if (arity == 2 && first.AsAtomId == DotAtomId)
            {
                cur = engine.NormalizeListCell(ResolveLocal(engine, restAfterFirst));
                engine.TryUnconsListLike(cur, out Cell consHead, out Cell afterHead);
                cur = engine.NormalizeListCell(ResolveLocal(engine, afterHead));
                engine.TryUnconsListLike(cur, out Cell consTail, out _);
                int consSlot = engine.AllocateHeap(2);
                engine.SetHeap(consSlot,     consHead);
                engine.SetHeap(consSlot + 1, consTail);
                return engine.UnifyRegisterWithCell(0, Cell.Lis(consSlot));
            }
            int functorId = FunctorTable.Intern(first.AsAtomId, arity);
            // Walk the list a second time to copy args into the STR.
            int strBase = engine.AllocateHeap(2 + arity);
            engine.SetHeap(strBase, Cell.Str(strBase + 1));
            engine.SetHeap(strBase + 1, Cell.Functor(functorId));
            // Skip the first element (functor name) and copy the rest.
            cur = engine.NormalizeListCell(ResolveLocal(engine, restAfterFirst));
            for (int i = 0; i < arity; i++)
            {
                engine.TryUnconsListLike(cur, out Cell argCell, out Cell argTail);
                engine.SetHeap(strBase + 2 + i, argCell);
                cur = engine.NormalizeListCell(ResolveLocal(engine, argTail));
            }
            return engine.UnifyRegisterWithCell(0, Cell.Ref(strBase));
        }
        return false;
    }

    /// <summary>Builds a fresh proper list whose head slots hold the given
    /// cell values. Same layout pattern as <c>SortBuiltins.BuildList</c>:
    /// 2N + 1 contiguous cells, alternating Lis / head pairs terminated
    /// by the empty-list atom.</summary>
    private static int BuildListFromCells(Activation engine, IReadOnlyList<Cell> elements)
    {
        if (elements.Count == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }
        int start = engine.AllocateHeap(2 * elements.Count + 1);
        for (int i = 0; i < elements.Count; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, elements[i]);
        }
        engine.SetHeap(start + 2 * elements.Count, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    /// <summary><c>term_to_atom(Term, Atom)</c> — bidirectional bridge
    /// between a Prolog term and its atom-text representation. With
    /// <c>Term</c> ground the term is rendered through <see cref="TermReader"/>
    /// (via the standard <see cref="Shumway.Builtins.TermRenderer"/> output)
    /// and the result interned as an atom. With <c>Atom</c> ground the atom
    /// text is parsed as a Prolog term via <see cref="Parser"/>.</summary>
    public static bool TermToAtom(Activation engine)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(1));

        if (atomCell.Tag == Tag.Atom)
        {
            // Atom → Term direction: parse the atom name as a Prolog term.
            string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            // The parser expects a clause-terminating dot; help it by
            // appending one when the user-supplied text doesn't have one.
            string source = name.TrimEnd().EndsWith(".", StringComparison.Ordinal)
                ? name
                : name + ".";
            Term parsed = ParseClauseText(engine, source);
            Cell newCell = Materializer.MaterializeAsCell(engine, parsed);
            return engine.UnifyRegisterWithCell(0, newCell);
        }

        // Term → Atom direction: render and intern. Match SWI's
        // term_to_atom/2 — render with operator notation (so `hola/2`
        // comes out as `hola/2`, not `/(hola, 2)`) and quoting (so the
        // atom round-trips back through the parser in the reverse
        // direction).
        using var sw = new System.IO.StringWriter();
        Shumway.Builtins.TermRenderer.Render(engine, engine.GetRegister(0), sw,
            new Shumway.Builtins.TermRenderOptions
            {
                Operators = engine.Operators,
                Quoted = true,
                // TightSymbolicOperators defaults true — symbolic ops
                // render space-free, matching other Prologs.
            });
        string rendered = sw.ToString();
        int newAtomId = AtomTable.Intern(rendered, permanent: false).Id;
        return engine.UnifyRegisterWithCell(1, Cell.Atom(newAtomId));
    }

    /// <summary><c>term_string(?Term, ?String[, +Options])</c> (SWI) — the
    /// string counterpart of <c>term_to_atom/2</c>: with <c>String</c> bound the
    /// text is parsed as a term; otherwise <c>Term</c> is rendered (operator
    /// notation, quoted) and unified with a fresh string. Options are accepted
    /// and currently ignored.</summary>
    /// <summary>The characters of a cons-cell text list — chars or codes.
    /// A packed one is read with ReadPstrChain; this is the other shape, and
    /// both denote the same list (ADR-047).</summary>
    private static string ReadTextListAsString(Activation engine, Cell list)
    {
        var sb = new System.Text.StringBuilder();
        Cell cur = list;
        while (engine.TryUnconsListLike(cur, out Cell rawHead, out Cell tail))
        {
            Cell head = ResolveLocal(engine, rawHead);
            if (head.Tag == Tag.Atom
                && AtomTable.GetById(head.AsAtomId)?.Name is { Length: 1 } n1)
                sb.Append(n1);
            else if (head.Tag == Tag.Int && head.AsInt >= 0 && head.AsInt <= char.MaxValue)
                sb.Append((char)head.AsInt);
            else
                throw new ShumwayPrologException(IsoError.TypeError("text",
                    MaterializeRegister(engine, 1)));
            cur = engine.NormalizeListCell(ResolveLocal(engine, tail));
        }
        return sb.ToString();
    }

    public static bool TermString(Activation engine)
    {
        Cell strCell = engine.NormalizeListCell(ResolveLocal(engine, engine.GetRegister(1)));
        if (Activation.IsListLike(strCell))
        {
            // Text → Term: read the characters and parse them. A cons list of
            // text reads the same as the packed one it denotes (ADR-047).
            string text = strCell.Tag == Tag.Pstr
                ? engine.ReadPstrChain(strCell, out _)
                : ReadTextListAsString(engine, strCell);
            string source = text.TrimEnd().EndsWith(".", StringComparison.Ordinal) ? text : text + ".";
            Term parsed = ParseClauseText(engine, source);
            Cell newCell = Materializer.MaterializeAsCell(engine, parsed);
            return engine.UnifyRegisterWithCell(0, newCell);
        }
        using var sw = new System.IO.StringWriter();
        Shumway.Builtins.TermRenderer.Render(engine, engine.GetRegister(0), sw,
            new Shumway.Builtins.TermRenderOptions { Operators = engine.Operators, Quoted = true });
        // The SWI string family produces text as a sequence of chars (ADR-047).
        return engine.UnifyRegisterWithCell(1, Cell.Ref(engine.MakePstr(sw.ToString(), TextKind.Chars)));
    }

    /// <summary><c>'$nb_setarg'(+Arg, +Term, +Value)</c> — the C# helper behind
    /// the SWI shim's <c>nb_setarg/3</c> / <c>nb_linkarg/3</c>: destructively
    /// links Term's Arg-th argument to Value, NOT trailed (survives
    /// backtracking). An atomic Value (the common mutable-counter case) is
    /// self-contained; a compound Value is linked as-is (it must outlive any
    /// backtrack that would reclaim it — the caller's responsibility, as in SWI's
    /// nb_linkarg).</summary>
    public static bool NbSetArg(Activation engine)
    {
        Cell iC = ResolveLocal(engine, engine.GetRegister(0));
        Cell tC = ResolveLocal(engine, engine.GetRegister(1));
        Cell vC = ResolveLocal(engine, engine.GetRegister(2));
        if (iC.Tag == Tag.Ref || tC.Tag == Tag.Ref)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (iC.Tag != Tag.Int)
            throw new ShumwayPrologException(IsoError.TypeError("integer", new VarTerm("_")));
        long i = iC.AsInt;
        int argSlot;
        int arity;
        if (tC.Tag == Tag.Str)
        {
            int functorIdx = tC.AsHeapIndex;
            (_, arity) = FunctorTable.Lookup(engine.GetHeap(functorIdx).AsFunctorId);
            argSlot = functorIdx + (int)i;   // args at functorIdx+1 … functorIdx+arity
        }
        else if (tC.Tag == Tag.Lis)
        {
            arity = 2;                       // '[|]'(Head, Tail)
            argSlot = tC.AsHeapIndex + (int)i - 1;
        }
        else
        {
            throw new ShumwayPrologException(IsoError.TypeError("compound", new VarTerm("_")));
        }
        if (i < 1 || i > arity) return false;
        engine.SetHeap(argSlot, vC);
        return true;
    }

    /// <summary><c>'$same_term'(@A, @B)</c> — the C# helper behind the SWI shim's
    /// <c>same_term/2</c>: A and B are the SAME term — the identical variable, the
    /// identical compound (same heap storage), or equal atomics. Distinct
    /// compounds with equal structure are NOT the same term.</summary>
    public static bool SameTerm(Activation engine)
    {
        Cell a = ResolveLocal(engine, engine.GetRegister(0));
        Cell b = ResolveLocal(engine, engine.GetRegister(1));
        if (a.Tag != b.Tag) return false;
        return a.Tag switch
        {
            Tag.Ref or Tag.AttVar or Tag.Str or Tag.Lis => a.AsHeapIndex == b.AsHeapIndex,
            Tag.Atom => a.AsAtomId == b.AsAtomId,
            Tag.Int => a.AsInt == b.AsInt,
            _ => Shumway.Builtins.StandardOrderComparator.Compare(engine, a, b) == 0,
        };
    }

    /// <summary><c>numbervars(+Term, +Start, -End, +Options)</c> (SWI) — as
    /// <c>numbervars/3</c> (arguments 1-3 identical); the option list is accepted
    /// and currently ignored (the default <c>'$VAR'(N)</c> numbering of every
    /// variable). Enough for the common <c>numbervars(T, 0, E, [])</c> call.</summary>
    public static bool NumberVars4(Activation engine) => NumberVars(engine);

    private static Cell ResolveLocal(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }

    // ============================================================================
    // numbervars/3
    // ============================================================================

    /// <summary><c>numbervars(Term, Start, End)</c> — walks <c>Term</c>
    /// left-to-right and binds every distinct unbound variable to a
    /// compound <c>'$VAR'(N)</c> with consecutive integers starting at
    /// <c>Start</c>. The next-free integer is unified with <c>End</c>.
    ///
    /// <para>Shared variables (same heap address visited twice) get the
    /// same number — the walk derefs each cell before deciding. Already-
    /// bound variables and non-variable subterms pass through unchanged.
    /// Mostly used to make terms presentable before printing or
    /// asserting.</para></summary>
    public static bool NumberVars(Activation engine)
    {
        Cell startC = engine.GetRegister(1);
        Cell startDeref = startC.Tag == Tag.Ref
            ? engine.GetHeap(engine.Deref(startC.AsHeapIndex))
            : startC;
        // ISO precedence — var second arg →
        // instantiation_error; bound non-int → type_error(integer, _).
        if (startDeref.Tag == Tag.Ref)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (startDeref.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException(
                "type_error", "integer", engine, startDeref);
        long start = startDeref.AsInt;

        // The End argument is an output, but a BOUND non-integer is still
        // type_error(integer, End) — it can never be the next free number.
        Cell endC = ResolveLocal(engine, engine.GetRegister(2));
        if (endC.Tag is not (Tag.Ref or Tag.AttVar or Tag.Int or Tag.BigInt))
            throw new Shumway.Core.PrologRuntimeException(
                "type_error", "integer", engine, endC);

        // Copy the input register to a heap slot so we have a stable address
        // to walk from. The walk visits each cell, derefs, and on the first
        // sight of an unbound REF binds it to a fresh '$VAR'(N) compound.
        int rootSlot = engine.AllocateHeap(1);
        engine.SetHeap(rootSlot, engine.GetRegister(0));

        var visited = new HashSet<int>();
        long counter = start;
        WalkAndNumber(engine, rootSlot, visited, ref counter);

        return engine.UnifyRegisterWithCell(2, Cell.Int(counter));
    }

    /// <summary><c>term_variables(+Term, -Variables)</c> — ISO §8.5.5. Unifies
    /// arg 2 with the list of distinct unbound variables of arg 1, in
    /// first-occurrence (depth-first, left-to-right) order. Shared and cyclic
    /// subterms are visited once (a <c>visited</c> address set).</summary>
    public static bool TermVariables(Activation engine)
    {
        // §8.5.5: the Vars argument must be a partial list —
        // `term_variables(foo, 3)` is type_error(list, 3), not a failure.
        {
            Cell cur = ResolveLocal(engine, engine.GetRegister(1));
            Cell given = cur;
            while (true)
            {
                if (cur.Tag is Tag.Ref or Tag.AttVar or Tag.Pstr) break;
                if (cur.Tag == Tag.Atom && cur.AsAtomId == AtomTable.EmptyListId) break;
                if (cur.Tag != Tag.Lis)
                    throw new Shumway.Core.PrologRuntimeException(
                        "type_error", "list", engine, given);
                cur = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex + 1));
            }
        }
        int rootSlot = engine.AllocateHeap(1);
        engine.SetHeap(rootSlot, engine.GetRegister(0));
        var visited = new HashSet<int>();
        var vars = new List<int>();
        CollectVars(engine, rootSlot, visited, vars);
        // Build [Ref(v0), ..., Ref(vn-1)] bottom-up (ADR-017 inline cons:
        // Cell.Lis(b) => heap[b]=head, heap[b+1]=tail).
        Cell tail = Cell.Atom(AtomTable.EmptyListId);
        for (int i = vars.Count - 1; i >= 0; i--)
        {
            int b = engine.AllocateHeap(2);
            engine.SetHeap(b, Cell.Ref(vars[i]));
            engine.SetHeap(b + 1, tail);
            tail = Cell.Lis(b);
        }
        return engine.UnifyRegisterWithCell(1, tail);
    }

    /// <summary>Pushes a compound's arguments onto <paramref name="work"/> so
    /// they pop in source order (last pushed is visited first).</summary>
    private static void PushArgsInOrder(Activation engine, Cell cell, List<int> work)
    {
        if (cell.Tag == Tag.Str)
        {
            int functorIdx = cell.AsHeapIndex;
            var (_, arity) = FunctorTable.Lookup(
                engine.GetHeap(functorIdx).AsFunctorId);
            for (int i = arity - 1; i >= 0; i--) work.Add(functorIdx + 1 + i);
        }
        else if (cell.Tag == Tag.Lis)
        {
            int headIdx = cell.AsHeapIndex;
            work.Add(headIdx + 1);
            work.Add(headIdx);
        }
    }

    // Both walks below are ITERATIVE over an explicit work list. A term is
    // user data of any depth, and recursion overflowed the C# stack — which
    // kills the process, not the query — at some ten thousand list elements.
    // Walking a list spine costs O(1) of that list: the tail replaces the
    // cons that pushed it.

    private static void CollectVars(
        Activation engine, int rootIdx, HashSet<int> visited, List<int> vars)
    {
        var work = new List<int>(32) { rootIdx };
        while (work.Count > 0)
        {
            int heapIdx = work[^1];
            work.RemoveAt(work.Count - 1);
            int addr = engine.Deref(heapIdx);
            if (!visited.Add(addr)) continue;
            Cell cell = engine.GetHeap(addr);
            // an attributed variable IS a variable (ISO/SWI); atoms, numbers
            // and packed strings are leaves with no variables.
            if (cell.Tag is Tag.Ref or Tag.AttVar) vars.Add(addr);
            else PushArgsInOrder(engine, cell, work);
        }
    }

    private static void WalkAndNumber(
        Activation engine, int rootIdx, HashSet<int> visited, ref long counter)
    {
        var work = new List<int>(32) { rootIdx };
        while (work.Count > 0)
        {
            int heapIdx = work[^1];
            work.RemoveAt(work.Count - 1);
            int addr = engine.Deref(heapIdx);
            if (!visited.Add(addr)) continue;
            Cell cell = engine.GetHeap(addr);
            if (cell.Tag == Tag.Ref)
            {
                // Unbound — bind to '$VAR'(counter).
                int varAtom = AtomTable.Intern("$VAR", permanent: true).Id;
                int functorId = FunctorTable.Intern(varAtom, 1);
                int strBase = engine.AllocateHeap(3);
                engine.SetHeap(strBase, Cell.Str(strBase + 1));
                engine.SetHeap(strBase + 1, Cell.Functor(functorId));
                engine.SetHeap(strBase + 2, Cell.Int(counter));
                counter++;
                // Bind addr to the new STR via a Ref to it (so trail catches it).
                int strRefSlot = engine.AllocateHeap(1);
                engine.SetHeap(strRefSlot, Cell.Ref(strBase));
                engine.Unify(addr, strRefSlot);
                continue;
            }
            PushArgsInOrder(engine, cell, work);
        }
    }

    // ============================================================================
    // clause/2, current_predicate/1, abolish/1
    // ============================================================================

    /// <summary><c>'$all_clauses_of'(HeadPattern, Pairs)</c> — returns a
    /// proper list of <c>Head-Body</c> pairs whose head functor matches
    /// the <em>functor</em> of <paramref name="HeadPattern"/>. Each
    /// returned head/body is a freshly materialised heap copy so the
    /// caller can unify with each pair's first element (the head) and
    /// then with the second element (the body) without sharing variable
    /// identity between candidates.
    ///
    /// <para>The prelude's <c>clause/2</c> uses this helper to fan out
    /// across candidates via <c>member/2</c>, so backtracking through
    /// matching clauses happens via the standard WAM choice-point
    /// machinery rather than through builtin-internal state.</para></summary>
    public static bool AllClausesOf(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$all_clauses_of'/2 requires a PrologEngine host.");

        Term headPattern = MaterializeRegister(engine, 0);
        int fid = ExtractCallableFunctorId(headPattern, "'$all_clauses_of'/2");

        var candidates = new List<Clause>();
        candidates.AddRange(host.DynamicClausesFor(fid));
        candidates.AddRange(host.StaticClausesFor(fid));

        // Build the list of '-/2'(Head, Body) pairs as AST terms, then
        // materialise the whole list onto the heap in one pass — that
        // way each candidate's variables stay independent of the others
        // and of the caller's head pattern.
        Term tail = new AtomTerm("[]");
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            var candidate = candidates[i];
            Term head = candidate.Kind == ClauseKind.Rule
                ? ((CompoundTerm)candidate.Term).Args[0]
                : candidate.Term;
            Term body = candidate.Kind == ClauseKind.Rule
                ? ((CompoundTerm)candidate.Term).Args[1]
                : new AtomTerm("true");
            // Pair shape `-(Head, Body)` matches how Prolog spells
            // `H-B` after operator parsing.
            Term pair = new CompoundTerm("-", new[] { head, body });
            tail = new CompoundTerm(".", new[] { pair, tail });
        }
        Cell listCell = Materializer.MaterializeAsCell(engine, tail);
        return engine.UnifyRegisterWithCell(1, listCell);
    }

    /// <summary><c>'$clause_enum'(Head, Head-Body)</c> — the LAZY backing for
    /// <c>clause/2</c>. The prelude passes the query's <c>Head-Body</c> pair as
    /// the second argument (built Prolog-side, so its variables are the user's),
    /// and this yields each matching clause one at a time on backtracking:
    /// per candidate it materialises just that clause's <c>-(Head, Body)</c>
    /// pair (head and body share variables, so the pair must be one
    /// materialisation) and unifies the query pair against it. Replaces
    /// <see cref="AllClausesOf"/> + <c>member/2</c>, which built the whole
    /// O(#clauses) pair list on the heap up front — here only the candidate
    /// being tried is on the heap, and a backtrack reclaims it.
    ///
    /// <para>The first register (Head) is used only to find the functor; the
    /// actual unification is against the pair in the second register, so the
    /// shared <c>Head</c> variable binds consistently from there.</para></summary>
    public static bool ClauseEnum(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$clause_enum'/2 requires a PrologEngine host.");

        Term headPattern = MaterializeRegister(engine, 0);
        int fid = ExtractCallableFunctorId(headPattern, "clause/2");

        var statics = new List<Clause>(host.StaticClausesFor(fid));
        // ISO §8.8.1.3: clause/2 reads PUBLIC procedures only — dynamic, or
        // static and declared `:- public` (the ISO public-procedure notion);
        // any other static user predicate is private — permission_error,
        // like GNU and Scryer. SWI lets programs inspect their own static
        // clauses, so an SWI-dialect caller keeps that; Arity has no such
        // restriction either.
        if (statics.Count > 0 && !host.IsDynamic(fid)
            && !host.IsDeclaredPublic(fid)
            && !host.Flags.ArityCompat
            && !host.CallerModuleHasDialect(engine, "swi"))
        {
            var (aId, ar) = FunctorTable.Lookup(fid);
            throw new ShumwayPrologException(IsoError.PermissionError(
                "access", "private_procedure",
                new CompoundTerm("/", new Term[]
                {
                    new AtomTerm(AtomTable.GetById(aId)?.Name ?? "?"),
                    new IntTerm(ar),
                })));
        }

        var candidates = new List<Clause>();
        candidates.AddRange(host.DynamicClausesFor(fid));
        candidates.AddRange(statics);
        // Drop the clauses whose head DEFINITELY cannot match the pattern, so
        // the cursor enumerates SOLUTIONS rather than the whole predicate —
        // first-argument indexing's answer to the same question, at the AST
        // level. `clause(p(1), B)` over `p(1). p(2).` is then deterministic.
        candidates.RemoveAll(c => !HeadCanMatch(headPattern, ClauseHead(c)));

        int returnPc = engine.BuiltinReturnPc;
        // arity 2 (clause/2): save the arg registers across backtracks.
        return Shumway.Core.IndexEnumCursor.Start(engine, candidates.Count, 2, returnPc,
            (e, i) => ClauseEnumUnify(e, candidates[i]));
    }

    private static Term ClauseHead(Clause c) => c.Kind == ClauseKind.Rule
        ? ((CompoundTerm)c.Term).Args[0]
        : c.Term;

    /// <summary>Conservative head prefilter: false only on a DEFINITE
    /// mismatch (both sides non-variable with different shapes). Anything it
    /// cannot rule out stays a candidate for the real unification.</summary>
    private static bool HeadCanMatch(Term pattern, Term head)
    {
        if (pattern is VarTerm || head is VarTerm) return true;
        if (pattern is CompoundTerm pc)
        {
            if (head is not CompoundTerm hc
                || pc.Functor != hc.Functor
                || pc.Args.Length != hc.Args.Length)
                return false;
            for (int i = 0; i < pc.Args.Length; i++)
                if (!ArgCanMatch(pc.Args[i], hc.Args[i])) return false;
            return true;
        }
        return ArgCanMatch(pattern, head);
    }

    private static bool ArgCanMatch(Term a, Term b) => (a, b) switch
    {
        (VarTerm, _) or (_, VarTerm) => true,
        (AtomTerm x, AtomTerm y) => x.Name == y.Name,
        (IntTerm x, IntTerm y) => x.Value == y.Value,
        (FloatTerm x, FloatTerm y) => x.Value.Equals(y.Value),
        (CompoundTerm x, CompoundTerm y) =>
            x.Functor == y.Functor && x.Args.Length == y.Args.Length,
        // Different leaf KINDS never unify; anything else stays a candidate.
        (AtomTerm, IntTerm) or (IntTerm, AtomTerm) => false,
        (AtomTerm or IntTerm or FloatTerm, CompoundTerm) => false,
        (CompoundTerm, AtomTerm or IntTerm or FloatTerm) => false,
        _ => true,
    };

    private static bool ClauseEnumUnify(Activation engine, Clause candidate)
        => ClauseEnumUnify(engine, candidate, pairRegister: 1);

    private static bool ClauseEnumUnify(Activation engine, Clause candidate, int pairRegister)
    {
        Term head = candidate.Kind == ClauseKind.Rule
            ? ((CompoundTerm)candidate.Term).Args[0]
            : candidate.Term;
        Term body = candidate.Kind == ClauseKind.Rule
            ? ((CompoundTerm)candidate.Term).Args[1]
            : new AtomTerm("true");
        // One materialisation so the clause's Head and Body share variables;
        // unify the query's Head-Body pair against it (the pair register is
        // 1 for '$clause_enum'/2, 2 for '$module_clause_enum'/3).
        Cell pairCell = Materializer.MaterializeAsCell(
            engine, new CompoundTerm("-", new[] { head, body }));
        return engine.UnifyRegisterWithCell(pairRegister, pairCell);
    }

    /// <summary><c>'$all_predicate_indicators'(List)</c> — returns a list
    /// of <c>Name/Arity</c> terms covering every predicate the engine
    /// knows about: builtins, dynamic functors, and static predicates
    /// from every loaded module. The prelude's <c>current_predicate/1</c>
    /// uses this helper to back-enumerate via <c>member/2</c>.</summary>
    public static bool AllPredicateIndicators(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$all_predicate_indicators'/1 requires a PrologEngine host.");

        var seen = new HashSet<int>();
        var indicators = new List<Term>();

        void AddIndicator(int functorId)
        {
            if (!seen.Add(functorId)) return;
            var (atomId, arity) = FunctorTable.Lookup(functorId);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            indicators.Add(new CompoundTerm("/",
                new Term[] { new AtomTerm(name), new IntTerm(arity) }));
        }

        // §8.8.2: current_predicate/1 enumerates USER-DEFINED procedures.
        // Builtins (and the prelude's library predicates, which are
        // built_in to a program) are excluded — GNU-verified:
        // current_predicate(atom/1) fails there too. predicate_property/2
        // is the way to ask about a builtin.
        foreach (int fid in host.AllStaticAndDynamicFunctors())
        {
            if (BuiltinsRegistry.TryGetByFunctor(fid, out _)) continue;
            if (host.IsPreludeFunctor(fid)) continue;
            AddIndicator(fid);
        }

        Term listTerm = new AtomTerm("[]");
        for (int i = indicators.Count - 1; i >= 0; i--)
            listTerm = new CompoundTerm(".", new[] { indicators[i], listTerm });
        Cell listCell = Materializer.MaterializeAsCell(engine, listTerm);
        return engine.UnifyRegisterWithCell(0, listCell);
    }

    /// <summary><c>'$current_predicate_enum'(?PI)</c> — the LAZY backing for
    /// <c>current_predicate/1</c>. Yields each known predicate's
    /// <c>Name/Arity</c> indicator one at a time on backtracking (a cursor
    /// over the snapshot), instead of building the whole O(n) indicator list
    /// on the heap up front for <c>member/2</c> to walk. Indicators are ground,
    /// so the per-step unification just filters against a bound <c>PI</c>.</summary>
    public static bool CurrentPredicateEnum(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$current_predicate_enum'/1 requires a PrologEngine host.");

        // A BOUND Name or Arity in the PI narrows the candidate set here, so
        // the cursor enumerates SOLUTIONS rather than every user predicate:
        // `current_predicate(foo/1)` is then deterministic instead of leaving
        // a choice point over the rest of the database.
        string? wantName = null;
        long? wantArity = null;
        {
            Cell pi = ResolveLocal(engine, engine.GetRegister(0));
            if (pi.Tag == Tag.Str)
            {
                int sa = pi.AsHeapIndex;
                Cell f = engine.GetHeap(sa);
                if (f.Tag == Tag.Functor)
                {
                    var (fAtom, fArity) = FunctorTable.Lookup(f.AsFunctorId);
                    if (fArity == 2 && AtomTable.GetById(fAtom)?.Name == "/")
                    {
                        Cell nCell = ResolveLocal(engine, engine.GetHeap(sa + 1));
                        Cell aCell = ResolveLocal(engine, engine.GetHeap(sa + 2));
                        if (nCell.Tag == Tag.Atom)
                            wantName = AtomTable.GetById(nCell.AsAtomId)?.Name;
                        if (aCell.Tag == Tag.Int) wantArity = aCell.AsInt;
                    }
                }
            }
        }

        var seen = new HashSet<int>();
        var indicators = new List<Term>();
        void AddIndicator(int functorId)
        {
            if (!seen.Add(functorId)) return;
            var (atomId, arity) = FunctorTable.Lookup(functorId);
            if (wantArity is { } wa && arity != wa) return;
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            if (wantName is { } wn && !string.Equals(name, wn, StringComparison.Ordinal))
                return;
            indicators.Add(new CompoundTerm("/",
                new Term[] { new AtomTerm(name), new IntTerm(arity) }));
        }
        // §8.8.2: current_predicate/1 enumerates USER-DEFINED procedures.
        // Builtins (and the prelude's library predicates, which are
        // built_in to a program) are excluded — GNU-verified:
        // current_predicate(atom/1) fails there too. predicate_property/2
        // is the way to ask about a builtin.
        foreach (int fid in host.AllStaticAndDynamicFunctors())
        {
            if (BuiltinsRegistry.TryGetByFunctor(fid, out _)) continue;
            if (host.IsPreludeFunctor(fid)) continue;
            AddIndicator(fid);
        }

        int returnPc = engine.BuiltinReturnPc;
        return Shumway.Core.IndexEnumCursor.Start(engine, indicators.Count, 1, returnPc,
            (e, i) => e.UnifyRegisterWithCell(0, Materializer.MaterializeAsCell(e, indicators[i])));
    }

    /// <summary><c>'$module_clause_enum'(+Module, +Head, ?Head-Body)</c> —
    /// the qualified <c>clause(M:H, B)</c>: the head resolved from M's
    /// VIEWPOINT. A dynamic is flat-global (the qualifier peels to the shared
    /// store); M's own definition reads M's clauses only; an import reads its
    /// source module's; anything else fails.</summary>
    public static bool ModuleClauseEnum(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$module_clause_enum'/3 requires a PrologEngine host.");
        if (MaterializeRegister(engine, 0) is not AtomTerm mod)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        Term headPattern = MaterializeRegister(engine, 1);
        int fid = ExtractCallableFunctorId(headPattern, "clause/2");

        var candidates = new List<Clause>();
        if (host.IsDynamic(fid))
            candidates.AddRange(host.DynamicClausesFor(fid));
        else if (host.ModuleDefinesFunctor(mod.Name, fid))
            candidates.AddRange(host.StaticClausesInModule(mod.Name, fid));
        else if (host.ModuleImportSource(mod.Name, fid) is { } src)
            candidates.AddRange(host.StaticClausesInModule(src, fid));

        int returnPc = engine.BuiltinReturnPc;
        return Shumway.Core.IndexEnumCursor.Start(engine, candidates.Count, 3, returnPc,
            (e, i) => ClauseEnumUnify(e, candidates[i], pairRegister: 2));
    }

    /// <summary><c>'$ctx_predicate_enum'(+Module, ?PI)</c> — the in-module
    /// view behind the context-injected <c>current_predicate/1</c>: the
    /// module's OWN definitions united with the global view, deduplicated.
    /// (The qualified form stays strictly per-module — SICStus doctrine;
    /// this union is only what an UNqualified call inside the module
    /// sees.)</summary>
    public static bool CtxPredicateEnum(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$ctx_predicate_enum'/2 requires a PrologEngine host.");
        Term modTerm = MaterializeRegister(engine, 0);
        var seen = new HashSet<(string, int)>();
        var indicators = new List<Term>();
        void Add(string name, int arity)
        {
            if (!seen.Add((name, arity))) return;
            indicators.Add(new CompoundTerm("/", new Term[]
                { new AtomTerm(name), new IntTerm(arity) }));
        }
        if (modTerm is AtomTerm ma)
            foreach (var (_, fid) in host.DefinedModulePredicates(ma.Name))
            {
                var (atomId, arity) = FunctorTable.Lookup(fid);
                Add(AtomTable.GetById(atomId)?.Name ?? "?", arity);
            }
        foreach (int fid in Shumway.Builtins.BuiltinsRegistry.AllRegisteredFunctorIds())
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            Add(AtomTable.GetById(atomId)?.Name ?? "?", arity);
        }
        foreach (int fid in host.AllStaticAndDynamicFunctors())
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            Add(AtomTable.GetById(atomId)?.Name ?? "?", arity);
        }
        int returnPc = engine.BuiltinReturnPc;
        return Shumway.Core.IndexEnumCursor.Start(engine, indicators.Count, 2, returnPc,
            (e, i) => e.UnifyRegisterWithCell(
                1, Materializer.MaterializeAsCell(e, indicators[i])));
    }

    /// <summary><c>'$module_predicate_enum'(?Module, ?PI)</c> — the backing
    /// for the qualified <c>current_predicate(M:PI)</c>. Enumerates
    /// (module, Name/Arity) over what each explicit module DEFINES (see
    /// <see cref="PrologEngine.DefinedModulePredicates"/>); a bound Module
    /// filters to that module (an unknown one just fails), an unbound one
    /// backtracks over the modules, SWI-style. Both positions unify per
    /// step, so a bound PI acts as a membership test.</summary>
    public static bool ModulePredicateEnum(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$module_predicate_enum'/2 requires a PrologEngine host.");
        Term modTerm = MaterializeRegister(engine, 0);
        string? onlyModule = modTerm is AtomTerm ma ? ma.Name : null;
        var modules = new List<Term>();
        var indicators = new List<Term>();
        foreach (var (mod, fid) in host.DefinedModulePredicates(onlyModule))
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            modules.Add(new AtomTerm(mod));
            indicators.Add(new CompoundTerm("/", new Term[]
            {
                new AtomTerm(AtomTable.GetById(atomId)?.Name ?? "?"),
                new IntTerm(arity),
            }));
        }
        int returnPc = engine.BuiltinReturnPc;
        return Shumway.Core.IndexEnumCursor.Start(engine, indicators.Count, 2, returnPc,
            (e, i) => e.UnifyRegisterWithCell(
                          0, Materializer.MaterializeAsCell(e, modules[i]))
                   && e.UnifyRegisterWithCell(
                          1, Materializer.MaterializeAsCell(e, indicators[i])));
    }

    /// <summary><c>'$listable_predicates'/1</c> — the user-defined
    /// predicates <c>listing/0,1</c> may print, each as a
    /// <c>pi(Name, Arity, Dynamic)</c> term where <c>Dynamic</c> is
    /// <c>true</c> or <c>false</c>. Builtins and the library predicates of
    /// <c>$prelude</c> / <c>clpfd</c> are excluded.</summary>
    public static bool ListablePredicates(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$listable_predicates'/1 requires a PrologEngine host.");

        var entries = new List<Term>();
        foreach (var (fid, isDynamic) in host.ListablePredicates())
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string mangled = AtomTable.GetById(atomId)?.Name ?? "?";
            // present the user-facing name. Local
            // predicates carry a "user$" (or other module) prefix
            // from ModuleRewrite; surface the unprefixed name so
            // `listing(foo)` finds the predicate the user wrote
            // as `foo(X) :- ...`.
            string name = PrologEngine.DemangleLocalName(mangled);
            entries.Add(new CompoundTerm("pi", new Term[]
            {
                new AtomTerm(name),
                new IntTerm(arity),
                new AtomTerm(isDynamic ? "true" : "false"),
            }));
        }

        Term listTerm = new AtomTerm("[]");
        for (int i = entries.Count - 1; i >= 0; i--)
            listTerm = new CompoundTerm(".", new[] { entries[i], listTerm });
        Cell listCell = Materializer.MaterializeAsCell(engine, listTerm);
        return engine.UnifyRegisterWithCell(0, listCell);
    }

    /// <summary><c>$listing_pred_source(+Name, +Arity)</c>.
    /// Prints every AST clause whose head functor matches
    /// <c>Name/Arity</c>, using <see cref="AstTermRenderer"/> so the
    /// original <see cref="Shumway.Compiler.Ast.VarTerm.Name"/> from
    /// the parser survives — the user sees <c>greet(X, Y) :- Y = hello(X)</c>
    /// instead of <c>greet(_G23, _G24) :- _G24 = hello(_G23)</c>.
    ///
    /// <para>The clauses come from both static-module sources
    /// (parsed by <c>ConsultString</c>, names preserved) and
    /// <c>:- dynamic foo/N. foo(a).</c>-seed clauses (also parsed
    /// from source). Runtime-asserted clauses arrive via the heap
    /// and carry synthetic <c>_G&lt;addr&gt;</c> names; this builtin
    /// renders whatever names the AST holds — preserved when source
    /// is available, synthetic otherwise.</para>
    ///
    /// <para>Output layout mirrors the prelude's portray_clause:
    /// facts on one line, rules with the head and an indented body
    /// line per goal.</para></summary>
    public static bool ListingPredSource(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$listing_pred_source'/2 requires a PrologEngine host.");

        Cell nameCell = MaterializeRegisterAsCell(engine, 0);
        Cell arityCell = MaterializeRegisterAsCell(engine, 1);
        if (nameCell.Tag != Tag.Atom || arityCell.Tag != Tag.Int)
            return false;
        string displayName = AtomTable.GetById(nameCell.AsAtomId)?.Name ?? "";
        int arity = (int)arityCell.AsInt;

        // ModuleRewrite mangles local predicates as
        // <module>$<name>. The user's `listing(helper)` arrives
        // here with the unmangled name; find every fid whose
        // demangled name matches (the predicate may be stored
        // under user$helper, foo$helper, or just helper if it's
        // public).
        var matchingFids = new List<int>();
        foreach (var (fid, _) in host.ListablePredicates())
        {
            var (atomId, fidArity) = FunctorTable.Lookup(fid);
            if (fidArity != arity) continue;
            string mangled = AtomTable.GetById(atomId)?.Name ?? "";
            if (mangled == displayName
                || PrologEngine.DemangleLocalName(mangled) == displayName)
                matchingFids.Add(fid);
        }

        var output = engine.Out;
        int printed = 0;
        foreach (int fid in matchingFids)
        {
            foreach (var clause in host.ClausesForListing(fid))
            {
                PrintAstClause(output, clause);
                printed++;
            }
            // no AST clauses but the predicate may still
            // exist as a precompiled record loaded from a source-
            // stripped bundle. Surface a comment so the user sees the
            // predicate is real — bare `true.` would lie by implying
            // there's no body to show when there are clauses, just
            // no source for them.
            if (printed == 0)
            {
                var pre = host.PrecompiledRecordFor(fid);
                if (pre is not null)
                {
                    string clauseWord = pre.ClauseCount == 1 ? "clause" : "clauses";
                    output.WriteLine(
                        $"% {displayName}/{arity}: {pre.ClauseCount} {clauseWord}, source stripped (no listing available)");
                    printed++;
                }
            }
        }
        return true;
    }

    /// <summary>delegates to the shared
    /// <see cref="ClausePortrayer"/>. The Clause's wrapping
    /// (Fact's bare head vs Rule's <c>:-(H,B)</c> compound) is
    /// detected by the portrayer from the Term's own shape — no
    /// need to thread <see cref="Shumway.Compiler.Ast.ClauseKind"/>
    /// through.</summary>
    private static void PrintAstClause(
        System.IO.TextWriter output, Shumway.Compiler.Ast.Clause clause)
    {
        ClausePortrayer.Print(output, clause.Term);
    }

    /// <summary><c>portray_clause(+Clause)</c>: prints
    /// Clause to the engine's current output using the standard
    /// portray layout (head + indented body goals, synthetic
    /// variables renumbered to A, B, C, …).</summary>
    public static bool PortrayClause1(Activation engine)
    {
        Term term = MaterializeRegister(engine, 0);
        ClausePortrayer.Print(engine.Out, term);
        return true;
    }

    /// <summary><c>portray_clause(+Stream, +Clause)</c>:
    /// like <see cref="PortrayClause1"/> but writes to the given
    /// output stream. The stream must be a Foreign cell bound to
    /// a write-mode handle (the same shape current_output / open
    /// produce).</summary>
    public static bool PortrayClause2(Activation engine)
    {
        TextWriter writer = ResolveTextWriter(engine, engine.GetRegister(0));
        Term term = MaterializeRegister(engine, 1);
        ClausePortrayer.Print(writer, term);
        return true;
    }

    /// <summary><c>abolish(Name/Arity)</c> — removes every asserted clause
    /// of the named dynamic predicate and unregisters it so subsequent
    /// assertions raise the "not declared dynamic" error until a new
    /// <c>:- dynamic</c> declaration arrives.</summary>
    public static bool Abolish(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "abolish/1 requires a PrologEngine host.");

        Term spec = MaterializeRegister(engine, 0);
        spec = StripPiQualifiers(spec);
        if (spec is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (spec is CompoundTerm c && c.Functor == "/" && c.Args.Length == 2)
        {
            // ISO §8.9.4.3 checks each slot before the indicator shape:
            // vars → instantiation; non-integer arity → type_error(integer);
            // non-atom name → type_error(atom); then the numeric range.
            if (c.Args[0] is VarTerm || c.Args[1] is VarTerm)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            if (c.Args[1] is not IntTerm and not BigIntTerm)
                throw new ShumwayPrologException(
                    IsoError.TypeError("integer", c.Args[1]));
            if (c.Args[0] is not AtomTerm name)
                throw new ShumwayPrologException(
                    IsoError.TypeError("atom", c.Args[0]));
            if (c.Args[1] is BigIntTerm big)
                throw new ShumwayPrologException(big.Value.Sign < 0
                    ? IsoError.DomainError("not_less_than_zero", big)
                    : IsoError.RepresentationError("max_procedure_arity"));
            var arity = (IntTerm)c.Args[1];
            if (arity.Value < 0)
                throw new ShumwayPrologException(
                    IsoError.DomainError("not_less_than_zero", arity));
            // An indicator past the PROCEDURE cap names nothing definable
            // (stc#70): terms are unbounded, predicates are not.
            if (arity.Value > Shumway.Core.RuntimeCaps.MaxProcedureArity)
                throw new ShumwayPrologException(
                    IsoError.RepresentationError("max_procedure_arity"));
            int fid = FunctorTable.Intern(
                AtomTable.Intern(name.Name, permanent: true).Id, (int)arity.Value);
            // Dynamic → abolish; builtin/static → permission_error (thrown
            // by the check); undefined → succeed silently (§8.9.4.1).
            if (host.IsAbolishModifiable(fid))
                host.AbolishDynamic(engine, fid);
            return true;
        }

        throw new ShumwayPrologException(
            IsoError.TypeError("predicate_indicator", spec));
    }

    /// <summary>Peels <c>Module:</c> qualifiers off a predicate-indicator
    /// argument (abolish/1): both <c>m:(N/A)</c> — the whole — and
    /// <c>(m:N)/A</c>, the operator parse of <c>m:N/A</c>. Dynamics are
    /// flat-global, so the qualifier validates and drops.</summary>
    private static Term StripPiQualifiers(Term spec)
    {
        spec = StripPiColonChain(spec);
        if (spec is CompoundTerm { Functor: "/", Args.Length: 2 } slash
            && slash.Args[0] is CompoundTerm { Functor: ":", Args.Length: 2 })
        {
            Term name = StripPiColonChain(slash.Args[0]);
            spec = new CompoundTerm("/", new[] { name, slash.Args[1] })
                { Position = slash.Position };
        }
        return spec;
    }

    private static Term StripPiColonChain(Term t)
    {
        while (t is CompoundTerm { Functor: ":", Args.Length: 2 } q)
        {
            switch (q.Args[0])
            {
                case AtomTerm: break;
                case VarTerm:
                    throw new ShumwayPrologException(IsoError.InstantiationError());
                default:
                    throw new ShumwayPrologException(
                        IsoError.TypeError("atom", q.Args[0]));
            }
            t = q.Args[1];
        }
        return t;
    }

    /// <summary><c>garbage_collect_clauses/0</c>. Walks
    /// every dynamic predicate's chain and re-threads it through only
    /// the live entries, bypassing the retracted ones still sitting
    /// in the bytecode. The dispatch cost of subsequent calls then
    /// drops from O(ever-asserted) back to O(live). The dead-clause
    /// bytecode is left orphaned; the program buffer doesn't shrink.</summary>
    public static bool GarbageCollectClauses0(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "garbage_collect_clauses/0 requires a PrologEngine host.");
        foreach (int fid in host.AllDynamicFunctors())
            host.GarbageCollectClauses(engine, fid);
        return true;
    }

    /// <summary><c>garbage_collect_clauses(+Name/Arity)</c>.
    /// Same as the 0-arg form but restricted to a single predicate.</summary>
    public static bool GarbageCollectClauses1(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "garbage_collect_clauses/1 requires a PrologEngine host.");
        Term spec = MaterializeRegister(engine, 0);
        if (spec is CompoundTerm c && c.Functor == "/" && c.Args.Length == 2
            && c.Args[0] is AtomTerm name && c.Args[1] is IntTerm arity)
        {
            int fid = FunctorTable.Intern(
                AtomTable.Intern(name.Name, permanent: true).Id, (int)arity.Value);
            host.GarbageCollectClauses(engine, fid);
            return true;
        }
        if (spec is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        throw new ShumwayPrologException(
            IsoError.TypeError("predicate_indicator", spec));
    }

    /// <summary><c>compact_dynamic_buffer/0</c>.
    /// Invalidates the persistent dynamic-code buffer so the next
    /// query rebuilds it from current <c>_dynamicClauses</c>.
    /// Reclaims memory consumed by chain entries and clause bodies
    /// appended by in-place assertz / asserta / retract that are no
    /// longer reachable from any current clause. The rebuild cost
    /// is one re-link of the dynamic region on the next query;
    /// chunks 155b-f then start fresh at append-only growth, so
    /// callers should invoke compaction periodically (e.g. after a
    /// large batch of mutations) rather than per-mutation.
    /// </summary>
    public static bool CompactDynamicBuffer(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "compact_dynamic_buffer/0 requires a PrologEngine host.");
        host.CompactDynamicCodeBuffer();
        return true;
    }

    /// <summary><c>compact_dynamic_buffer(+Name/Arity)</c> — Phase-12
    /// Per-predicate variant. Validates the predicate
    /// indicator, errors on bad inputs (instantiation /
    /// type_error / domain_error / permission_error for non-
    /// dynamic), then falls through to the same full rebuild as
    /// the 0-arg form. The persistent buffer holds every dynamic
    /// predicate's bytecode interleaved, so independent per-
    /// predicate compaction isn't feasible without partial-relink
    /// support — the API surface is per-predicate as a forward-
    /// compatibility hint.</summary>
    public static bool CompactDynamicBuffer1(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "compact_dynamic_buffer/1 requires a PrologEngine host.");
        Term spec = MaterializeRegister(engine, 0);
        if (spec is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (spec is not CompoundTerm c || c.Functor != "/" || c.Args.Length != 2
            || c.Args[0] is not AtomTerm nameAtom || c.Args[1] is not IntTerm arityInt)
            throw new ShumwayPrologException(
                IsoError.TypeError("predicate_indicator", spec));
        int fid = FunctorTable.Intern(
            AtomTable.Intern(nameAtom.Name, permanent: true).Id, (int)arityInt.Value);
        if (!host.IsDynamic(fid))
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "modify,static_procedure");
        host.CompactDynamicCodeBuffer();
        return true;
    }

}
