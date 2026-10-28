namespace Cut.UpdateEngine;

public static class UpdatePaths
{
    public static string DefaultData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cut");
    public static string Data { get; private set; } = DefaultData;
    public static string? TestKey { get; private set; }
    public static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("缺少参数：" + name);
        return args[index + 1];
    }
    public static string Configure(string[] args)
    {
        var data = Argument(args, "--data-dir");
        var key = Argument(args, "--test-instance-key");
        if (key is not null)
        {
            if (data is null || !key.StartsWith("Cut.Tests.", StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(data, ".cut-update-test")) || File.ReadAllText(Path.Combine(data, ".cut-update-test")) != key)
                throw new IOException("隔离测试需要明确标记的配置目录。");
            Data = Path.GetFullPath(data); TestKey = key;
        }
        else
        {
            if (data is not null && !Path.GetFullPath(data).Equals(DefaultData, StringComparison.OrdinalIgnoreCase))
                throw new IOException("正式客户端只使用当前用户的 Cut 配置目录。");
            Data = DefaultData; TestKey = null;
        }
        return Data;
    }
}
