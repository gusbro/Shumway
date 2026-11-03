namespace Shumway.Core;

/// <summary>
/// An object an activation reaches that keeps atom ids outside heap cells: the
/// cursor of a builtin's choice point, an entry of the foreign table. A mark of the
/// activation adds them (ADR-003); an id it leaves out can die while the object
/// still uses it.
/// </summary>
public interface IAtomHolder
{
    void MarkAtoms(AtomMarkSet set);
}
