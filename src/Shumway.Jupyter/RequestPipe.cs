namespace Shumway.Jupyter;

/// <summary>The request lines of a session, as the stream the server reads
/// them from. A read blocks the engine thread until a line arrives or the
/// pipe is completed, which the server sees as end of file.</summary>
internal sealed class RequestPipe : TextReader
{
    // A plain object: System.Threading.Lock does not exclude on browser-wasm
    // with threads.
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private string _line = "";
    private int _at;
    private bool _completed;

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line + "\n");
            Monitor.PulseAll(_gate);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Peek()
    {
        lock (_gate) return Available() ? _line[_at] : -1;
    }

    public override int Read()
    {
        lock (_gate) return Available() ? _line[_at++] : -1;
    }

    // The base class fills the whole buffer, which would wait for requests
    // that have not been sent: hand out what the current line has.
    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_gate)
        {
            if (count == 0 || !Available()) return 0;
            int taken = Math.Min(count, _line.Length - _at);
            _line.CopyTo(_at, buffer, index, taken);
            _at += taken;
            return taken;
        }
    }

    private bool Available()
    {
        while (_at >= _line.Length)
        {
            if (_lines.Count > 0) { _line = _lines.Dequeue(); _at = 0; }
            else if (_completed) return false;
            else Monitor.Wait(_gate);
        }
        return true;
    }
}
