using System.Text.Json;

namespace Flattiverse.Gateway.Protocol;

public readonly struct PayloadElement
{
    private readonly object? _value;

    public PayloadElement(object? value) => _value = value;

    public JsonValueKind ValueKind => _value switch
    {
        null => JsonValueKind.Null,
        string => JsonValueKind.String,
        true => JsonValueKind.True,
        false => JsonValueKind.False,
        IDictionary<object, object> => JsonValueKind.Object,
        IDictionary<string, object> => JsonValueKind.Object,
        object[] => JsonValueKind.Array,
        IList<object> => JsonValueKind.Array,
        _ when IsNumber(_value) => JsonValueKind.Number,
        _ => JsonValueKind.Undefined
    };

    public string? GetString() => _value switch
    {
        string s => s,
        null => null,
        _ => _value.ToString()
    };

    public float GetSingle() => Convert.ToSingle(_value);

    public double GetDouble() => Convert.ToDouble(_value);

    public int GetInt32() => Convert.ToInt32(_value);

    public PayloadElement GetProperty(string name)
    {
        if (TryGetProperty(name, out var value))
            return value;
        throw new KeyNotFoundException($"Property '{name}' not found.");
    }

    public bool TryGetProperty(string name, out PayloadElement value)
    {
        if (_value is IDictionary<object, object> objDict)
        {
            if (objDict.TryGetValue(name, out var val))
            {
                value = new PayloadElement(val);
                return true;
            }
        }
        else if (_value is IDictionary<string, object> strDict)
        {
            if (strDict.TryGetValue(name, out var val))
            {
                value = new PayloadElement(val);
                return true;
            }
        }

        value = default;
        return false;
    }

    public IEnumerable<PayloadElement> EnumerateArray()
    {
        if (_value is object[] arr)
            return arr.Select(o => new PayloadElement(o));
        if (_value is IList<object> list)
            return list.Select(o => new PayloadElement(o));
        return Enumerable.Empty<PayloadElement>();
    }

    private static bool IsNumber(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;
}
