using System.Text;
using System.Text.Json;
using XiaomiPerformance;

Console.OutputEncoding = new UTF8Encoding(false);
AppOptions options;
try { options = AppOptions.Parse(args, "status"); }
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
if (options.Command == "help")
{
    Console.WriteLine("""
    小米性能模式 CLI（原厂服务适配器）
    XiaomiPerformance.Cli status [--json]
    XiaomiPerformance.Cli set eco|smart|turbo|quiet|rage [--json]
    XiaomiPerformance.Cli next [--json]

    模拟必须显式增加 --simulate；--state-file <路径> 指定独立模拟状态文件。
    示例：XiaomiPerformance.Cli next --simulate --state-file .\demo.json --json
    模拟文件不存在时，初始模拟模式为智能。默认 status 只读取管家发布状态；set/next 经原厂服务执行。
    0.3.0：额定供电为静谧/智能/狂暴，其他条件为省电/智能/极速；next自动跟随当前原厂组。
    绿色档位联动 Windows 节能；切回蓝色/金色恢复此前设置。status 和模拟模式不改变系统设置。
    托盘自动规则：接电且电量高于95%时使用原厂金色档并关闭节能；CLI 不自动执行此规则。

    退出码：0 成功；2 参数错误；3 状态未知；4 未启用写入；5 忙；6 失败；7 取消/超时。
    """);
    return 0;
}
if (options.Command == "tray") { Console.Error.WriteLine("请运行 XiaomiPerformance.Tray.exe 打开托盘。"); return 2; }
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var controller = BackendFactory.CreateController(options);
var result = options.Command switch
{
    "set" => await controller.SetAsync(ParseMode(options.Mode!), cancellation.Token),
    "next" => await controller.NextAsync(cancellation.Token),
    _ => await controller.RefreshAsync(cancellation.Token)
};
if (options.Json) Console.WriteLine(JsonSerializer.Serialize(new
{
    result.Code, result.Status, result.Message, result.Success, energySaver = controller.EnergySaver
}, JsonFormat.Options));
else
{
    Console.WriteLine($"{(controller.IsSimulation ? "[模拟，非电脑真实状态] " : "")}{result.Status.ModeLabel} · {result.Status.Backend}");
    Console.WriteLine(result.Message);
}
return result.Code switch
{
    OperationCode.Ok => 0, OperationCode.Unknown => 3, OperationCode.ReadOnly => 4,
    OperationCode.Busy => 5, OperationCode.Failed => 6, OperationCode.Cancelled => 7, _ => 6
};
static PerformanceMode ParseMode(string value) { PerformanceModes.TryParse(value, out var mode); return mode; }
