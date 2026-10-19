using System.Buffers.Binary;
using System.Text;

namespace MusicBox.Core.Archives;

/// <summary>
/// Minimal Python pickle reader. Only the opcodes that appear in Ren'Py RPA
/// indexes are implemented (protocols 0–5, dictionaries/lists/tuples/bytes).
/// </summary>
internal sealed class PickleReader
{
    private readonly byte[] _data;
    private int _pos;
    private readonly Stack<object?> _stack = new();
    private readonly Dictionary<int, object?> _memo = new();
    private readonly List<object?> _marks = new();

    public PickleReader(byte[] data) => _data = data;

    public object? Read()
    {
        while (_pos < _data.Length)
        {
            byte opcode = _data[_pos++];
            switch (opcode)
            {
                case 0x80: // PROTO
                    _pos++;
                    break;
                case 0x95: // FRAME
                    _pos += 8;
                    break;
                case 0x2E: // STOP
                    return _stack.Count > 0 ? _stack.Pop() : null;
                case 0x28: // MARK
                    _marks.Add(_stack.Count);
                    break;
                case 0x7D: // EMPTY_DICT
                    _stack.Push(new Dictionary<object, object?>());
                    break;
                case 0x5D: // EMPTY_LIST
                    _stack.Push(new List<object?>());
                    break;
                case 0x29: // EMPTY_TUPLE
                    _stack.Push(Array.Empty<object?>());
                    break;
                case 0x85: // TUPLE1
                    PushTuple(1);
                    break;
                case 0x86: // TUPLE2
                    PushTuple(2);
                    break;
                case 0x87: // TUPLE3
                    PushTuple(3);
                    break;
                case 0x74: // TUPLE
                    {
                        int mark = PopMark();
                        int count = _stack.Count - mark;
                        var items = PopItems(count);
                        _stack.Push(items);
                        break;
                    }
                case 0x64: // DICT
                    {
                        int mark = PopMark();
                        var items = PopItems(_stack.Count - mark);
                        var dict = new Dictionary<object, object?>();
                        for (int i = 0; i + 1 < items.Length; i += 2) dict[items[i]!] = items[i + 1];
                        _stack.Push(dict);
                        break;
                    }
                case 0x73: // SETITEM
                    {
                        object? value = Pop();
                        object? key = Pop();
                        if (Peek() is Dictionary<object, object?> d) d[key!] = value;
                        break;
                    }
                case 0x75: // SETITEMS
                    {
                        int mark = PopMark();
                        var items = PopItems(_stack.Count - mark);
                        if (Peek() is Dictionary<object, object?> d)
                        {
                            for (int i = 0; i + 1 < items.Length; i += 2) d[items[i]!] = items[i + 1];
                        }
                        break;
                    }
                case 0x61: // APPEND
                    {
                        object? value = Pop();
                        if (Peek() is List<object?> list) list.Add(value);
                        break;
                    }
                case 0x65: // APPENDS
                    {
                        int mark = PopMark();
                        var items = PopItems(_stack.Count - mark);
                        if (Peek() is List<object?> list) list.AddRange(items);
                        break;
                    }
                case 0x4A: // BININT (signed 4)
                    _stack.Push((long)BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos)));
                    _pos += 4;
                    break;
                case 0x4B: // BININT1
                    _stack.Push((long)_data[_pos++]);
                    break;
                case 0x4D: // BININT2
                    _stack.Push((long)BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos)));
                    _pos += 2;
                    break;
                case 0x49: // INT (text line)
                    _stack.Push(ParseTextInteger(ReadLine()));
                    break;
                case 0x4C: // LONG
                    {
                        var line = ReadLine().TrimEnd('L');
                        _stack.Push(long.TryParse(line, out var v) ? v : 0L);
                        break;
                    }
                case 0x8A: // LONG1
                    {
                        int length = _data[_pos++];
                        _stack.Push(ReadLittleEndianLong(length));
                        break;
                    }
                case 0x8B: // LONG4
                    {
                        int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos));
                        _pos += 4;
                        _stack.Push(ReadLittleEndianLong(length));
                        break;
                    }
                case 0x47: // BINFLOAT
                    {
                        var bits = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(_pos));
                        _pos += 8;
                        _stack.Push(BitConverter.Int64BitsToDouble(bits));
                        break;
                    }
                case 0x42: // BINBYTES
                    {
                        int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos));
                        _pos += 4;
                        _stack.Push(_data.AsSpan(_pos, length).ToArray());
                        _pos += length;
                        break;
                    }
                case 0x43: // SHORT_BINBYTES
                    {
                        int length = _data[_pos++];
                        _stack.Push(_data.AsSpan(_pos, length).ToArray());
                        _pos += length;
                        break;
                    }
                case 0x8E: // BINBYTES8
                    {
                        long length = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(_pos));
                        _pos += 8;
                        _stack.Push(_data.AsSpan(_pos, (int)length).ToArray());
                        _pos += (int)length;
                        break;
                    }
                case 0x54: // BINSTRING (bytes in py3)
                    {
                        int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos));
                        _pos += 4;
                        _stack.Push(_data.AsSpan(_pos, length).ToArray());
                        _pos += length;
                        break;
                    }
                case 0x55: // SHORT_BINSTRING
                    {
                        int length = _data[_pos++];
                        _stack.Push(_data.AsSpan(_pos, length).ToArray());
                        _pos += length;
                        break;
                    }
                case 0x58: // BINUNICODE
                    {
                        int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos));
                        _pos += 4;
                        _stack.Push(Encoding.UTF8.GetString(_data, _pos, length));
                        _pos += length;
                        break;
                    }
                case 0x8C: // SHORT_BINUNICODE
                    {
                        int length = _data[_pos++];
                        _stack.Push(Encoding.UTF8.GetString(_data, _pos, length));
                        _pos += length;
                        break;
                    }
                case 0x8D: // BINUNICODE8
                    {
                        long length = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(_pos));
                        _pos += 8;
                        _stack.Push(Encoding.UTF8.GetString(_data, _pos, (int)length));
                        _pos += (int)length;
                        break;
                    }
                case 0x96: // BYTEARRAY8
                    {
                        long length = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(_pos));
                        _pos += 8;
                        _stack.Push(_data.AsSpan(_pos, (int)length).ToArray());
                        _pos += (int)length;
                        break;
                    }
                case 0x94: // MEMOIZE
                    _memo[_memo.Count] = Peek();
                    break;
                case 0x71: // BINPUT
                    _memo[_data[_pos++]] = Peek();
                    break;
                case 0x72: // LONG_BINPUT
                    _memo[BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos))] = Peek();
                    _pos += 4;
                    break;
                case 0x70: // PUT
                    _memo[int.Parse(ReadLine())] = Peek();
                    break;
                case 0x67: // GET
                    _stack.Push(_memo.GetValueOrDefault(int.Parse(ReadLine())));
                    break;
                case 0x30: // POP
                    Pop();
                    break;
                case 0x31: // POP_MARK
                    PopItems(_stack.Count - PopMark());
                    break;
                case 0x32: // DUP
                    _stack.Push(Peek());
                    break;
                case 0x68: // BINGET
                    _stack.Push(_memo.GetValueOrDefault(_data[_pos++]));
                    break;
                case 0x6A: // LONG_BINGET
                    _stack.Push(_memo.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos))));
                    _pos += 4;
                    break;
                case 0x88: // NEWTRUE
                    _stack.Push(true);
                    break;
                case 0x89: // NEWFALSE
                    _stack.Push(false);
                    break;
                case 0x4E: // NONE
                    _stack.Push(null);
                    break;
                case 0x56: // UNICODE (text line)
                    _stack.Push(ReadLine());
                    break;
                case 0x53: // STRING (text line with quotes)
                    _stack.Push(Unquote(ReadLine()));
                    break;
                case 0x63: // GLOBAL — not used in RPA indexes
                case 0x69: // INST
                case 0x6F: // OBJ
                    throw new NotSupportedException("Unsupported pickle opcode.");
                default:
                    throw new NotSupportedException($"Unknown pickle opcode 0x{opcode:X2}.");
            }
        }

        return _stack.Count > 0 ? _stack.Pop() : null;
    }

    private object? Peek() => _stack.Count > 0 ? _stack.Peek() : null;

    private object? Pop() => _stack.Count > 0 ? _stack.Pop() : null;

    private int PopMark()
    {
        if (_marks.Count == 0) return 0;
        int mark = (int)_marks[^1]!;
        _marks.RemoveAt(_marks.Count - 1);
        return mark;
    }

    private object?[] PopItems(int count)
    {
        var items = new object?[count];
        for (int i = count - 1; i >= 0; i--) items[i] = Pop();
        return items;
    }

    private void PushTuple(int count)
    {
        var items = PopItems(count);
        _stack.Push(items);
    }

    private string ReadLine()
    {
        int start = _pos;
        while (_pos < _data.Length && _data[_pos] != (byte)'\n') _pos++;
        string line = Encoding.UTF8.GetString(_data, start, _pos - start);
        if (_pos < _data.Length) _pos++;
        return line;
    }

    private static long ParseTextInteger(string text)
    {
        text = text.Trim();
        if (text.EndsWith('L')) text = text[..^1];
        return long.TryParse(text, out var value) ? value : 0;
    }

    private long ReadLittleEndianLongFrom(byte[] data, int offset, int length)
    {
        long value = 0;
        for (int i = Math.Min(length, 8) - 1; i >= 0; i--) value = (value << 8) | data[offset + i];
        return value;
    }

    private long ReadLittleEndianLong(int length)
    {
        long value = ReadLittleEndianLongFrom(_data, _pos, length);
        _pos += length;
        return value;
    }

    private static string Unquote(string text)
    {
        if (text.Length >= 2 && (text[0] == '\'' || text[0] == '"')) return text[1..^1];
        return text;
    }
}
