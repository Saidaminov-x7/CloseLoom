using System.Diagnostics;
using System.Text.Json;
using CloseLoom.Core.Models;
using CloseLoom.Core.Services;
using CloseLoom.Core.Policies;
using CloseLoom.Infrastructure.Persistence;
using CloseLoom.Infrastructure.Windows;
using CloseLoom.Infrastructure.Logging;
using CloseLoom.Infrastructure.Monitoring;
using CloseLoom.Core.Interfaces;

var dir = Path.GetFullPath(args[0]);
var fixture = Path.GetFullPath(args[1]);
Directory.CreateDirectory(dir);
var control = Path.Combine(dir, "fixture-command.txt");
var allowlist = new JsonApplicationAllowlist(Path.Combine(dir,"allowlist.json"));
var prefs = new JsonUserSettingsStore(Path.Combine(dir,"settings.json"));
var logs = new JsonLinesActivityLog(Path.Combine(dir,"activity.jsonl"));
var counted = new CountingActivityLog(logs);
var processes = new WindowsProcessCatalog();
var windows = new WindowsWindowMonitor();
var terminator = new WindowsProcessTerminator();
var safe = new SafeTerminationService(allowlist,processes,windows,terminator,counted,new TerminationPolicy());
var coordinator = new GracePeriodCoordinator(safe,counted);
var source = new WindowsWindowEventSource();
await using var monitor = new WindowsApplicationMonitor(processes,windows,allowlist,prefs,coordinator,source,countingLog:counted);
monitor.StatusChanged += (_,st) => Console.WriteLine($"STATUS running={st.IsRunning} paused={st.IsPaused} tracked={st.TrackedProcesses} error={st.LastError}");
var checks = new List<string>();
Process? proc = null;
var failures = new List<string>();
async Task Assert(string name, Func<Task<bool>> predicate, int timeoutMs=6000)
{
 var now=Stopwatch.StartNew();
 bool ok=false;
 while(now.ElapsedMilliseconds<timeoutMs)
 {
  try{if(await predicate()){ok=true;break;}}catch{}
  await Task.Delay(130);
 }
 var status=(ok?"PASS ":"FAIL ")+name;
 Console.WriteLine(status);
 checks.Add(status);
 if(!ok)failures.Add(name);
}
async Task<bool> HasEvent(ActivityEventType type, Func<CloseLoom.Core.Models.ActivityEvent,bool>? filter=null)
{
 var evs=await logs.ReadAsync();
 return evs.Any(e=>e.Type==type && (filter==null || filter(e)));
}
async Task<int> CountEvent(ActivityEventType type)
{
 return (await logs.ReadAsync()).Count(e=>e.Type==type);
}
async Task Command(string cmd)
{
 var id=cmd+"|"+Guid.NewGuid().ToString("N");
 await File.WriteAllTextAsync(control,id);
 await Assert("Fixture ACK "+cmd,async()=>File.Exists(control+".ack") && (await File.ReadAllTextAsync(control+".ack"))==id,2500);
}
async Task<int> WinCount() => proc==null ? -1 : (await windows.GetWindowsAsync(proc.Id,CancellationToken.None)).Count(w=>w.IsVisible);
try
{
 await prefs.SaveAsync(new UserSettings { DryRun=true,GracePeriodSeconds=3,CloseBehavior="exit",Language="en" });
 await allowlist.AddOrUpdateAsync(new MonitoredApplication(Guid.NewGuid(),"Isolated LIVE Fixture",fixture,true,TimeSpan.FromSeconds(3),true,true));
 await Assert("Allowlist contains ONLY isolated executable",async()=> (await allowlist.ListAsync()).Count==1);
 await monitor.StartAsync();
 await Assert("Real WinEvent hooks started + Dry Run active",()=>Task.FromResult(monitor.Status.IsRunning && monitor.Status.DryRunEnabled && !monitor.Status.IsPaused));
 proc=Process.Start(new ProcessStartInfo(fixture){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(fixture)!,ArgumentList={control}});
 if(proc==null)throw new Exception("Fixture process failed to start");
 Console.WriteLine("TEST_FIXTURE_PID "+proc.Id);
 await Assert("Native fixture window visible",async()=>await WinCount()==1);
 await Assert("ProcessDetected written by real monitor",async()=>await HasEvent(ActivityEventType.ProcessDetected,e=>e.ProcessId==proc.Id),7000);
 await Assert("Tracked process count reaches 1",()=>Task.FromResult(monitor.Status.TrackedProcesses==1));
 int original=await CountEvent(ActivityEventType.GracePeriodStarted);
 await Command("MINIMIZE");await Task.Delay(2700);
 await Assert("Minimizing does NOT start grace period",async()=>(await CountEvent(ActivityEventType.GracePeriodStarted))==original);
 await Command("RESTORE");
 await Command("SHOW_SECOND");
 await Assert("Two visible windows in same process",async()=>await WinCount()==2);
 await Command("CLOSE_MAIN");
 await Assert("Second window remains visible after first closes",async()=>await WinCount()==1);
 await Task.Delay(2500);
 await Assert("Closing first of two does NOT start grace",async()=>(await CountEvent(ActivityEventType.GracePeriodStarted))==original);
 await Command("REOPEN_MAIN");
 await Assert("Main window reopened with second visible",async()=>await WinCount()==2);
 await Command("CLOSE_SECOND");
 await Assert("Second closed; main remains",async()=>await WinCount()==1);
 await Command("CLOSE_MAIN");
 await Assert("Last real window closed; process remains alive",async()=>!proc.HasExited && await WinCount()==0);
 await Assert("GracePeriodStarted after last window closed",async()=>await CountEvent(ActivityEventType.GracePeriodStarted)>original,6500);
 await Task.Delay(350);
 var beforeCancel=await CountEvent(ActivityEventType.GracePeriodCancelled);
 await Command("REOPEN_MAIN");
 await Assert("Reopening cancels pending grace",async()=>await CountEvent(ActivityEventType.GracePeriodCancelled)>beforeCancel,5000);
 await Task.Delay(3250);
 await Assert("Reopened window prevents premature Dry Run",async()=>!(await logs.ReadAsync()).Any(e=>e.Type==ActivityEventType.ProcessSkipped && e.Result=="Dry run"));
 var secondGrace=await CountEvent(ActivityEventType.GracePeriodStarted);
 await Command("CLOSE_MAIN");
 await Assert("Second grace begins after real close",async()=>await CountEvent(ActivityEventType.GracePeriodStarted)>secondGrace,6500);
 await Assert("Dry-run decision logged after 3 seconds",async()=>await HasEvent(ActivityEventType.ProcessSkipped,e=>e.Result=="Dry run" && e.ProcessId==proc.Id),6800);
 await Assert("No actual kill; fixture remains running",()=>Task.FromResult(!proc.HasExited));
 await Assert("No terminated event",async()=>!(await logs.ReadAsync()).Any(e=>e.Type==ActivityEventType.ProcessTerminated));
 var completedDecisions=(await logs.ReadAsync()).Count(e=>e.Type==ActivityEventType.ProcessSkipped && e.Result=="Dry run");
 await Task.Delay(7500); // Two full reconciliation/grace windows; a stable background-only process must not be re-evaluated.
 await Assert("Dry Run occurs exactly once without a new main window",async()=> (await logs.ReadAsync()).Count(e=>e.Type==ActivityEventType.ProcessSkipped && e.Result=="Dry run")==completedDecisions);
 await Assert("Stable windowless process stays alive during 7.5 second cooldown",()=>Task.FromResult(!proc.HasExited));
 await Command("EXIT");
 await Assert("Fixture exited on its own",()=>Task.FromResult(proc.HasExited),2500);
 await Assert("Exit logged",async()=>await HasEvent(ActivityEventType.ProcessExited,e=>e.ProcessId==proc.Id),6500);
 Console.WriteLine($"BEFORE_PAUSE_STATUS {monitor.Status}");
 await monitor.PauseAsync();
 await Assert("Pause transitions to paused",()=>Task.FromResult(monitor.Status.IsPaused),3000);
 await monitor.ResumeAsync();
 await Assert("Resume reinstalls real hooks",()=>Task.FromResult(monitor.Status.IsRunning && !monitor.Status.IsPaused),4000);
 await monitor.StopAsync();
 await Assert("Stop removes active state",()=>Task.FromResult(!monitor.Status.IsRunning),3000);
 await monitor.StartAsync();
 await Assert("Fresh restart succeeds",()=>Task.FromResult(monitor.Status.IsRunning),4000);
 await monitor.StopAsync();
 await Assert("Second stop succeeds",()=>Task.FromResult(!monitor.Status.IsRunning),3000);
}
catch(Exception error)
{
 failures.Add("EXCEPTION "+error.GetType().Name+": "+error.Message);
 Console.WriteLine("EXCEPTION "+error);
}
finally
{
 try{if(monitor.Status.IsRunning)await monitor.StopAsync();}catch(Exception e){Console.WriteLine("STOP_EXCEPTION "+e.Message);}
 if(proc is { HasExited:false })
 {
  try {await Command("EXIT");await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}
  catch
  {
   Console.WriteLine("FIXTURE_DID_NOT_EXIT_GRACEFULLY_"+proc.Id);
   try
   {
    // Only the isolated fixture launched by this runner may be cleaned up.
    if(!proc.HasExited && string.Equals(proc.MainModule?.FileName, fixture, StringComparison.OrdinalIgnoreCase))
    {
     proc.Kill(entireProcessTree:false);
     await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
   }
   catch(Exception cleanupError) { Console.WriteLine("FIXTURE_CLEANUP_ERROR "+cleanupError.GetType().Name); }
  }
 }
 var events=await logs.ReadAsync();
 foreach(var e in events)
  Console.WriteLine($"EVENT {e.Type} RESULT={e.Result} REASON={e.Reason} PID={e.ProcessId}");
 await File.WriteAllTextAsync(Path.Combine(dir,"summary.json"),JsonSerializer.Serialize(new {success=failures.Count==0,checks,failures,eventCount=events.Count,dryRun=true,actualTerminationTested=false,fixturePid=proc?.Id},new JsonSerializerOptions{WriteIndented=true}));
 Console.WriteLine($"RESULT PASS={checks.Count(x=>x.StartsWith("PASS"))} FAIL={checks.Count(x=>x.StartsWith("FAIL"))} EVENTS={events.Count} FAILURES={failures.Count}");
}
if(failures.Count>0) Environment.ExitCode=1;
