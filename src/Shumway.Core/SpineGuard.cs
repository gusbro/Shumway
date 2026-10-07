namespace Shumway.Core;

/// <summary>Cycle detection for a walk down a list spine that does its own
/// stepping: give it the resolved cell the walk starts from, then every
/// resolved tail it moves to; <see cref="Loops"/> answers true once the walk
/// is back on a cell it has been on, which only a cyclic spine does. Brent's
/// method: one comparison per step, the cycle found within twice its length
/// past its entry. A walk that does not ask never ends on a cyclic list, and
/// a C# loop that never ends passes no safe point: not even a cancel stops it.
/// </summary>
public struct SpineGuard
{
    private Cell _saved;
    private long _power, _lam;

    public SpineGuard(Cell start)
    {
        _saved = start;
        _power = 1;
        _lam = 0;
    }

    public bool Loops(Cell cur)
    {
        if (cur.Equals(_saved)) return true;
        if (++_lam == _power)
        {
            _saved = cur;
            _power <<= 1;
            _lam = 0;
        }
        return false;
    }
}
