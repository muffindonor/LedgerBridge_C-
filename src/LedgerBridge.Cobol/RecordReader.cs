namespace LedgerBridge.Cobol;

/// <summary>
/// Reads records one at a time through a reused buffer, so a file of any size
/// needs the same small amount of memory. <see cref="Current"/> is only valid
/// until the next call to <see cref="Read"/>.
/// </summary>
public sealed class RecordReader : IDisposable
{
    private const int DefaultBufferSize = 1 << 20;

    private readonly FileStream _stream;
    private readonly byte[] _buffer;
    private readonly byte[] _padded;
    private readonly int _recordLength;
    private readonly bool _lineSequential;
    private readonly string _path;
    private int _position;
    private int _end;
    private int _currentStart;
    private bool _currentIsPadded;
    private bool _endOfFile;

    private RecordReader(string path, int recordLength, bool lineSequential, int bufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, recordLength + 2);
        _path = path;
        _recordLength = recordLength;
        _lineSequential = lineSequential;
        _buffer = new byte[bufferSize];
        _padded = new byte[recordLength];
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
    }

    /// <summary>The number of the current record, counting from 1.</summary>
    public long RecordNumber { get; private set; }

    public ReadOnlySpan<byte> Current =>
        _currentIsPadded ? _padded : _buffer.AsSpan(_currentStart, _recordLength);

    /// <summary>
    /// Newline-delimited text, as COBOL LINE SEQUENTIAL files are. GnuCOBOL drops trailing
    /// spaces when it writes these, so short lines are padded back to the full record length.
    /// </summary>
    public static RecordReader OpenLineSequential(string path, int recordLength, int bufferSize = DefaultBufferSize) =>
        new(path, recordLength, lineSequential: true, bufferSize);

    /// <summary>Fixed-length binary records with no delimiters, as a mainframe extract is.</summary>
    public static RecordReader OpenFixed(string path, int recordLength, int bufferSize = DefaultBufferSize) =>
        new(path, recordLength, lineSequential: false, bufferSize);

    public bool Read()
    {
        bool found = _lineSequential ? ReadLine() : ReadFixed();
        if (found)
        {
            RecordNumber++;
        }

        return found;
    }

    public void Dispose() => _stream.Dispose();

    private bool ReadFixed()
    {
        if (_end - _position < _recordLength)
        {
            Fill();
            int available = _end - _position;
            if (available == 0)
            {
                return false;
            }

            if (available < _recordLength)
            {
                throw new RecordFormatException(
                    $"{Path.GetFileName(_path)}: record {RecordNumber + 1} is cut short at {available} of {_recordLength} bytes.");
            }
        }

        _currentStart = _position;
        _currentIsPadded = false;
        _position += _recordLength;
        return true;
    }

    private bool ReadLine()
    {
        int lineEnd = _buffer.AsSpan(_position, _end - _position).IndexOf((byte)'\n');
        if (lineEnd < 0)
        {
            Fill();
            lineEnd = _buffer.AsSpan(_position, _end - _position).IndexOf((byte)'\n');
        }

        int lineLength;
        int consumed;
        if (lineEnd >= 0)
        {
            lineLength = lineEnd;
            consumed = lineEnd + 1;
        }
        else
        {
            // Fill always leaves room for a whole line, so no newline means end of file.
            lineLength = _end - _position;
            consumed = lineLength;
            if (lineLength == 0)
            {
                return false;
            }
        }

        if (lineLength > 0 && _buffer[_position + lineLength - 1] == '\r')
        {
            lineLength--;
        }

        if (lineLength > _recordLength)
        {
            throw new RecordFormatException(
                $"{Path.GetFileName(_path)}: record {RecordNumber + 1} has {lineLength} bytes, more than the {_recordLength} in its layout.");
        }

        ReadOnlySpan<byte> line = _buffer.AsSpan(_position, lineLength);
        if (line.Contains((byte)0))
        {
            throw new RecordFormatException(
                $"{Path.GetFileName(_path)}: record {RecordNumber + 1} contains a NUL byte, which a text record can't hold.");
        }

        if (lineLength == _recordLength)
        {
            _currentStart = _position;
            _currentIsPadded = false;
        }
        else
        {
            line.CopyTo(_padded);
            _padded.AsSpan(lineLength).Fill((byte)' ');
            _currentIsPadded = true;
        }

        _position += consumed;
        return true;
    }

    private void Fill()
    {
        int remaining = _end - _position;
        if (remaining > 0 && _position > 0)
        {
            _buffer.AsSpan(_position, remaining).CopyTo(_buffer);
        }

        _position = 0;
        _end = remaining;
        while (!_endOfFile && _end < _buffer.Length)
        {
            int read = _stream.Read(_buffer, _end, _buffer.Length - _end);
            if (read == 0)
            {
                _endOfFile = true;
            }

            _end += read;
        }
    }
}
