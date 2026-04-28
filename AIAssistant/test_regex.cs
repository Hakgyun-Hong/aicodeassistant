using System;
using System.Text.RegularExpressions;

public class Program
{
    public static void Main()
    {
        string json1 = "{ \"canFix\": \"true\" }";
        string json2 = "{ canFix: true }";
        string json3 = "{ \"canFix\": true }";

        Console.WriteLine("json1: " + GetJsonBool(json1, "canFix"));
        Console.WriteLine("json2: " + GetJsonBool(json2, "canFix"));
        Console.WriteLine("json3: " + GetJsonBool(json3, "canFix"));
    }

    private static bool GetJsonBool(string json, string key)
    {
        var match = Regex.Match(json, $"\"{key}\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
        return match.Success && match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
