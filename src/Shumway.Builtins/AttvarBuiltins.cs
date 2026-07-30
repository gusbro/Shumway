using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The attributed-variable access predicates. An
/// attributed variable is an unbound variable that additionally carries
/// a set of (module, value) attribute pairs. <c>put_attr/3</c>,
/// <c>get_attr/3</c> and <c>del_attr/2</c> are the surface for
/// attaching, reading and removing those pairs; the <c>attvar/1</c>
/// type test lives in <see cref="TypeBuiltins.IsAttVar"/>.
///
/// <para>The attributes themselves live in the engine's attribute table
/// — keyed by a module's atom id, valued by the heap index of the
/// attribute term. Every mutation is trailed, so attributes attached or
/// removed inside a choice point revert on backtracking. Unifying an
/// attributed variable with a value runs the module's
/// <c>verify_attributes/4</c> hook.</para>
/// </summary>
public static class AttvarBuiltins
{
    /// <summary><c>put_attr(-Var, +Module, +Value)</c> — attaches (or
    /// replaces) <c>Module</c>'s attribute on <c>Var</c>, setting it to
    /// <c>Value</c>. A plain unbound variable is promoted to an
    /// attributed variable in place. Throws <c>type_error(var, _)</c>
    /// when <c>Var</c> is already bound to a non-variable.</summary>
    public static bool PutAttr(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        int valueAddr = RegisterToHeap(engine, 2);
        if (AttrVerify) VerifyTerm(engine, valueAddr, "put_attr", varAddr, moduleId);
        engine.PutAttr(varAddr, moduleId, valueAddr);
        return true;
    }

    // ---- library(atts) storage primitives (native) --------------------
    // Each atts module keeps a LIST of its attribute terms as the variable's
    // put_attr/get_attr value; these three walk/rebuild that list in C#. The
    // Prolog shim's walks ($attr_find/$attr_exclude + functor/3 per element)
    // were the hottest predicates of a clpz solve. Representation unchanged:
    // list cells on the heap, mutation through PutAttr/DelAttr (trailed).

    /// <summary><c>'$put_to_attr_list'(V, Module, Attr)</c> — replaces (or
    /// adds) the attribute term with <c>Attr</c>'s functor in <c>Module</c>'s
    /// list on <c>V</c>.</summary>
    public static bool PutToAttrList(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        var (kind, key) = AttrTermKey(engine, engine.GetRegister(2));
        int listIdx = engine.GetAttr(varAddr, moduleId);
        var kept = CollectNonMatching(engine, listIdx, kind, key, out _);
        Cell regCell = engine.GetRegister(2);
        Cell headCell = regCell.Tag is Tag.Ref or Tag.AttVar
            ? Cell.Ref(RegisterToHeap(engine, 2)) : regCell;
        engine.PutAttr(varAddr, moduleId,
            BuildAttrList(engine, headCell, kept));
        return true;
    }

    /// <summary><c>'$get_from_attr_list'(V, Module, Attr)</c> — finds the
    /// list element sharing <c>Attr</c>'s functor and unifies. Semidet: the
    /// first functor match decides.</summary>
    public static bool GetFromAttrList(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        int listIdx = engine.GetAttr(varAddr, moduleId);
        if (listIdx < 0) return false;
        var (kind, key) = AttrTermKey(engine, engine.GetRegister(2));
        int cursor = engine.Deref(listIdx);
        Cell cell = engine.GetHeap(cursor);
        while (cell.Tag == Tag.Lis)
        {
            int pair = cell.AsHeapIndex;
            if (MatchesKey(engine, engine.GetHeap(pair), kind, key))
                return engine.UnifyRegisterWithHeapAt(2, pair);
            cell = engine.GetHeap(pair + 1);
            if (cell.Tag == Tag.Ref) cell = engine.GetHeap(engine.Deref(cell.AsHeapIndex));
        }
        return false;
    }

    /// <summary><c>'$del_from_attr_list'(V, Module, Attr)</c> — removes the
    /// element sharing <c>Attr</c>'s functor. Always succeeds; a miss (or a
    /// non-attributed <c>V</c>) is a no-op, and removing the last element
    /// drops the module's attribute entirely.</summary>
    public static bool DelFromAttrList(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        int listIdx = engine.GetAttr(varAddr, moduleId);
        if (listIdx < 0) return true;
        var (kind, key) = AttrTermKey(engine, engine.GetRegister(2));
        var kept = CollectNonMatching(engine, listIdx, kind, key, out bool removedAny);
        if (!removedAny) return true;
        if (kept.Count == 0) engine.DelAttr(varAddr, moduleId);
        else engine.PutAttr(varAddr, moduleId, BuildAttrList(engine, null, kept));
        return true;
    }

    // The functor identity an atts operation keys on: (1, functorId) for
    // compounds and atoms, (2, raw cell) for other constants — mirroring what
    // functor/3-based matching distinguished. An unbound Attr is an error,
    // exactly like the shim's functor(Attr, F, A).
    private static (int Kind, long Key) AttrTermKey(Activation engine, Cell c)
    {
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        return c.Tag switch
        {
            Tag.Ref or Tag.AttVar =>
                throw new PrologRuntimeException("instantiation_error"),
            Tag.Str => (1, engine.GetHeap(c.AsHeapIndex).AsFunctorId),
            Tag.Atom => (1, FunctorTable.Intern(c.AsAtomId, 0)),
            _ => (2, unchecked((long)c.Data ^ ((long)c.Tag << 56))),
        };
    }

    private static bool MatchesKey(Activation engine, Cell head, int kind, long key)
    {
        if (head.Tag == Tag.Ref)
            head = engine.GetHeap(engine.Deref(head.AsHeapIndex));
        return head.Tag switch
        {
            Tag.Str => kind == 1 && engine.GetHeap(head.AsHeapIndex).AsFunctorId == key,
            Tag.Atom => kind == 1 && FunctorTable.Intern(head.AsAtomId, 0) == key,
            Tag.Ref or Tag.AttVar => false,
            _ => kind == 2 && unchecked((long)head.Data ^ ((long)head.Tag << 56)) == key,
        };
    }

    // Walks the module's list collecting the head CELLS whose functor does
    // not match — the kept elements share structure with the old list.
    private static System.Collections.Generic.List<Cell> CollectNonMatching(
        Activation engine, int listIdx, int kind, long key, out bool removedAny)
    {
        var kept = new System.Collections.Generic.List<Cell>();
        removedAny = false;
        if (listIdx < 0) return kept;
        Cell cell = engine.GetHeap(engine.Deref(listIdx));
        while (cell.Tag == Tag.Lis)
        {
            int pair = cell.AsHeapIndex;
            Cell head = engine.GetHeap(pair);
            if (MatchesKey(engine, head, kind, key)) removedAny = true;
            else kept.Add(head.Tag is Tag.Ref or Tag.AttVar ? Cell.Ref(pair) : head);
            cell = engine.GetHeap(pair + 1);
            if (cell.Tag == Tag.Ref) cell = engine.GetHeap(engine.Deref(cell.AsHeapIndex));
        }
        return kept;
    }

    // Builds [newHead? | kept...] bottom-up (ADR-017 inline cons) and returns
    // the heap index of a cell holding the list root.
    private static int BuildAttrList(
        Activation engine, Cell? newHead, System.Collections.Generic.List<Cell> kept)
    {
        Cell tail = Cell.Atom(AtomTable.EmptyListId);
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            int pair = engine.AllocateHeap(2);
            engine.SetHeap(pair, kept[i]);
            engine.SetHeap(pair + 1, tail);
            tail = Cell.Lis(pair);
        }
        if (newHead is { } h)
        {
            int pair = engine.AllocateHeap(2);
            engine.SetHeap(pair, h);
            engine.SetHeap(pair + 1, tail);
            tail = Cell.Lis(pair);
        }
        int root = engine.AllocateHeap(1);
        engine.SetHeap(root, tail);
        return root;
    }

    // ---- SHUMWAY_ATTR_VERIFY=1 tripwire (debug aid) ----
    private static readonly bool AttrVerify =
        System.Environment.GetEnvironmentVariable("SHUMWAY_ATTR_VERIFY") == "1";

    /// <summary>Walks the term rooted at <paramref name="rootIdx"/> checking
    /// cell well-formedness (a Str payload must address a Functor cell; list
    /// pairs recurse; indices must be in range). Throws a catchable
    /// system_error naming the offending address so the REPL prints the
    /// Prolog stack of the writer/reader.</summary>
    private static void VerifyTerm(
        Activation engine, int rootIdx, string site, int varAddr, int moduleId)
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        var stack = new System.Collections.Generic.Stack<int>();
        stack.Push(rootIdx);
        while (stack.Count > 0)
        {
            int idx = stack.Pop();
            if (idx < 0 || idx >= engine.HeapTop)
                Fail(engine, site, varAddr, moduleId, idx, "index out of range");
            if (!seen.Add(idx)) continue;
            Cell c = engine.GetHeap(idx);
            switch (c.Tag)
            {
                case Tag.Ref:
                case Tag.AttVar:
                    if (c.AsHeapIndex != idx) stack.Push(c.AsHeapIndex);
                    break;
                case Tag.Str:
                {
                    int f = c.AsHeapIndex;
                    if (f < 0 || f >= engine.HeapTop
                        || engine.GetHeap(f).Tag != Tag.Functor
                        || !Shumway.Core.FunctorTable.TryLookup(
                               engine.GetHeap(f).AsFunctorId, out var fe))
                    {
                        Fail(engine, site, varAddr, moduleId, idx,
                            $"Str -> heap[{f}] tag={(f >= 0 && f < engine.HeapTop ? engine.GetHeap(f).Tag.ToString() : "?")}");
                        break;
                    }
                    else
                    {
                        for (int a = 1; a <= fe.Arity; a++) stack.Push(f + a);
                    }
                    break;
                }
                case Tag.Lis:
                {
                    int p = c.AsHeapIndex;
                    if (p < 0 || p + 1 >= engine.HeapTop)
                        Fail(engine, site, varAddr, moduleId, idx, $"Lis -> heap[{p}] out of range");
                    stack.Push(p);
                    stack.Push(p + 1);
                    break;
                }
                default:
                    break;   // atoms/ints/floats/functor-in-place: fine as leaves
            }
        }
    }

    private static void Fail(
        Activation engine, string site, int varAddr, int moduleId, int idx, string what)
    {
        string mod = Shumway.Core.AtomTable.GetById(moduleId)?.Name ?? "?";
        System.Console.Error.WriteLine(
            $"[ATTR-VERIFY] {site}: malformed term at heap[{idx}] ({what}) var@{varAddr} module={mod} heapTop={engine.HeapTop}");
        throw new PrologRuntimeException("system_error", $"attr_verify_{site}");
    }

    /// <summary><c>get_attr(+Var, +Module, -Value)</c> — unifies
    /// <c>Value</c> with the attribute <c>Module</c> carries on
    /// <c>Var</c>. Fails silently when <c>Var</c> has no such attribute
    /// (or isn't an attributed variable at all).</summary>
    public static bool GetAttr(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        int valueAddr = engine.GetAttr(varAddr, moduleId);
        if (AttrVerify && valueAddr >= 0)
            VerifyTerm(engine, valueAddr, "get_attr", varAddr, moduleId);
        return valueAddr >= 0 && engine.UnifyRegisterWithHeapAt(2, valueAddr);
    }

    /// <summary><c>del_attr(+Var, +Module)</c> — removes <c>Module</c>'s
    /// attribute from <c>Var</c>. Always succeeds, even when <c>Var</c>
    /// carries no such attribute or isn't an attributed variable.</summary>
    public static bool DelAttr(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        engine.DelAttr(varAddr, moduleId);
        return true;
    }

    /// <summary><c>'$attr_modules'(+Var, -Modules)</c> — the list of module atoms
    /// under which <c>Var</c> carries an attribute (empty if none / not an
    /// attributed variable). The engine-level enumeration the SICStus/Scryer
    /// <c>library(atts)</c> shim needs to build <c>'$get_attr_list'</c>; the rest
    /// of that API is Prolog over put_attr / get_attr / del_attr.</summary>
    public static bool AttrModules(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        var modules = engine.AttrModules(varAddr);
        int list = BuildAtomIdList(engine, modules);
        return engine.UnifyRegisterWithHeapAt(1, list);
    }

    // Builds a proper list of atoms (by id) on the heap, ADR-017 inline cons.
    private static int BuildAtomIdList(Activation engine, IReadOnlyCollection<int> atomIds)
    {
        int count = atomIds.Count;
        if (count == 0)
        {
            int nil = engine.AllocateHeap(1);
            engine.SetHeap(nil, Cell.Atom(AtomTable.EmptyListId));
            return nil;
        }
        int start = engine.AllocateHeap(2 * count + 1);
        int i = 0;
        foreach (int id in atomIds)
        {
            int lisIdx = start + 2 * i;
            engine.SetHeap(lisIdx, Cell.Lis(lisIdx + 1));
            engine.SetHeap(lisIdx + 1, Cell.Atom(id));
            i++;
        }
        engine.SetHeap(start + 2 * count, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    /// <summary>Resolves an argument register to a heap index. A
    /// register holding a REF — the usual shape for a variable argument
    /// — already names a heap cell; an immediate is copied onto a fresh
    /// heap cell. Mirrors the engine's own register materialisation, so
    /// a non-variable passed where a variable is required reaches
    /// <see cref="Activation.PutAttr"/>'s type check intact.</summary>
    private static int RegisterToHeap(Activation engine, int regIdx)
    {
        Cell c = engine.GetRegister(regIdx);
        // REF and ATTVAR both carry a heap home index as payload.
        if (c.Tag is Tag.Ref or Tag.AttVar) return c.AsHeapIndex;
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, c);
        return slot;
    }

    /// <summary>Reads the module argument — required to be a bound atom
    /// — and returns its atom id. Throws <c>instantiation_error</c> for
    /// an unbound module and <c>type_error(atom, _)</c> for a non-atom.</summary>
    private static int ModuleId(Activation engine, int regIdx)
    {
        Cell c = engine.GetRegister(regIdx);
        if (c.Tag == Tag.Ref)
            c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        return c.Tag switch
        {
            Tag.Atom => c.AsAtomId,
            Tag.Ref or Tag.AttVar =>
                throw new PrologRuntimeException("instantiation_error"),
            _ => throw new PrologRuntimeException("type_error", "atom"),
        };
    }
}
