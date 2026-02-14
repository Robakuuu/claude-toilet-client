namespace ClaudeToiletClient.Services;

/// <summary>
/// Thread-safe ring buffer that retains the last N characters of terminal output.
/// </summary>
public class ScrollbackBuffer
{
    private readonly char[] _buffer;
    private int _start;
    private int _length;
    private readonly object _lock = new();

    public ScrollbackBuffer(int capacity = 102_400) // ~100KB
    {
        _buffer = new char[capacity];
    }

    public void Append(string data)
    {
        lock (_lock)
        {
            foreach (var ch in data)
            {
                int writePos = (_start + _length) % _buffer.Length;
                _buffer[writePos] = ch;

                if (_length < _buffer.Length)
                    _length++;
                else
                    _start = (_start + 1) % _buffer.Length;
            }
        }
    }

    public string GetContents()
    {
        lock (_lock)
        {
            if (_length == 0) return string.Empty;

            var result = new char[_length];
            for (int i = 0; i < _length; i++)
            {
                result[i] = _buffer[(_start + i) % _buffer.Length];
            }
            return new string(result);
        }
    }
}
