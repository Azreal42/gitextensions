using System.Text;

namespace GitExtensions.Linux;

/// <summary>Decodes Git's C-quoted pathname output where a command provides no NUL format.</summary>
public static class GitQuotedPath
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.StartsWith('"'))
        {
            return value;
        }

        if (value.Length < 2 || !value.EndsWith('"'))
        {
            throw new FormatException("Incomplete Git quoted pathname.");
        }

        using MemoryStream bytes = new();
        int plainStart = 1;
        for (int index = 1; index < value.Length - 1; index++)
        {
            if (value[index] != '\\')
            {
                continue;
            }

            bytes.Write(Utf8.GetBytes(value[plainStart..index]));
            if (++index >= value.Length - 1)
            {
                throw new FormatException("Incomplete escape in Git quoted pathname.");
            }

            if (value[index] is >= '0' and <= '7')
            {
                int number = 0;
                int count = 0;
                while (index < value.Length - 1 && value[index] is >= '0' and <= '7' && count < 3)
                {
                    number = (number * 8) + value[index] - '0';
                    index++;
                    count++;
                }

                if (number > byte.MaxValue)
                {
                    throw new FormatException("Git octal pathname escape exceeds one byte.");
                }

                bytes.WriteByte((byte)number);
                index--;
            }
            else
            {
                bytes.WriteByte(value[index] switch
                {
                    'a' => 7,
                    'b' => 8,
                    't' => 9,
                    'n' => 10,
                    'v' => 11,
                    'f' => 12,
                    'r' => 13,
                    '\\' => 92,
                    '"' => 34,
                    _ => throw new FormatException("Unknown escape in Git quoted pathname."),
                });
            }

            plainStart = index + 1;
        }

        bytes.Write(Utf8.GetBytes(value[plainStart..^1]));
        return Utf8.GetString(bytes.ToArray());
    }
}
