using Microsoft.ML.OnnxRuntime;

Console.WriteLine("=== ONNX Runtime + DirectML on this machine ===\n");
Console.WriteLine($"ORT version    : {typeof(SessionOptions).Assembly.GetName().Version}");
Console.WriteLine($"Available EPs  : {string.Join(", ", OrtEnv.Instance().GetAvailableProviders())}\n");

// Try to actually create a DirectML session option set on each adapter.
for (int dev = 0; dev < 2; dev++)
{
    try
    {
        var so = new SessionOptions();
        so.AppendExecutionProvider_DML(dev);
        Console.WriteLine($"DirectML device {dev}: session options created OK");
        so.Dispose();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"DirectML device {dev}: {ex.GetType().Name} - {ex.Message.Split('\n')[0]}");
    }
}
Console.WriteLine("\n=== done ===");
