using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using ProbablyCoop;

internal static class Check
{
    internal static int Actor=-1, Draws, Checks;
    internal static bool Terminal;
    internal static Func<bool> Eligible=()=>true;
    internal static Action OnDraw;
    internal static List<string> Events=new List<string>();
    internal static readonly HarmonyLib.Harmony Harmony=new HarmonyLib.Harmony();
    internal static readonly MethodInfo Request=typeof(NativeActorNight).GetMethod("RequestLottery",BindingFlags.Static|BindingFlags.NonPublic);
    internal static void RequestDraw()
    {
        try { Request.Invoke(null,null); }
        catch(TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
    }
    internal static void Require(bool condition,string label)
    { Checks++; if(!condition)throw new Exception("FAIL "+label); }
    internal static void Throws(Action body,string label)
    { bool threw=false;try{body();}catch{threw=true;}Require(threw,label); }
    internal static IDisposable Enter(int actor)
    { int old=Actor;Actor=actor;return new Exit(()=>Actor=old); }
    internal sealed class Exit:IDisposable
    { private readonly Action action;internal Exit(Action action)=>this.action=action;public void Dispose()=>action(); }
    internal static void Reset()
    { Actor=-1;Draws=0;Terminal=false;Eligible=()=>true;OnDraw=null;Events.Clear(); }
    internal static NativeActorNight.Result Execute(NativeActorNight n,HealthData h,HealthData p,Func<int,IDisposable> enter=null,Func<bool> valid=null)
        =>n.ExecuteForDay(5,h,p,enter??Enter,valid??(()=>true),()=>Terminal);

    internal static void Main()
    {
        Reset();var host=new HealthData();var partner=new HealthData();
        Throws(()=>Execute(new NativeActorNight("missing"),host,partner),"not installed rejected");
        Require(host.Calls+partner.Calls==0,"install gate prevents native health calls");
        RequestDraw();Require(Draws==1,"outside cooperative scope forwards native draw");
        NativeActorNight.Install(Harmony);Require(NativeActorNight.IsInstalled,"explicit install only");

        Reset();var normal=new NativeActorNight("normal");host=new HealthData();partner=new HealthData();
        Require(Execute(normal,host,partner)==NativeActorNight.Result.Completed,"normal completion");
        Require(host.Calls==1&&partner.Calls==1&&Draws==1,"two health phases one draw");
        Require(Events.SequenceEqual(new[]{"health:0","health:1","draw:0"}),"host-context draw follows both personal phases");
        Require(Actor==-1&&normal.GetAttempt(5).CompletedActorMask==3&&normal.GetAttempt(5).LotteryCompleted,"scope and receipt complete");
        Require(Execute(normal,host,partner)==NativeActorNight.Result.AlreadyCompleted&&Draws==1&&host.Calls==1,"same-day replay rejected without mutation");

        Reset();Eligible=()=>Actor==1;host=new HealthData{Due=false};partner=new HealthData();
        var guestUnlock=new NativeActorNight("guest-perk");Execute(guestUnlock,host,partner);
        Require(guestUnlock.GetAttempt(5).LotteryRequested&&!guestUnlock.GetAttempt(5).LotteryAttempted&&Draws==0,"partner gambling perk cannot unlock host automatic draw");
        Reset();host=new HealthData{Due=false};partner=new HealthData{Due=false};Execute(new NativeActorNight("not-due"),host,partner);
        Require(Draws==0,"native day/eligibility branch can request no draw");

        Reset();host=new HealthData{Before=()=>Terminal=true};partner=new HealthData();var endHost=new NativeActorNight("host-ending");
        Require(Execute(endHost,host,partner)==NativeActorNight.Result.Terminal&&host.Calls==1&&partner.Calls==0&&Draws==0,"host ending prevents partner and lottery");
        Terminal=false;Require(Execute(endHost,host,partner)==NativeActorNight.Result.Terminal&&partner.Calls==0,"terminal receipt stays terminal");
        Require(Actor==-1,"terminal restores actor context");
        Reset();host=new HealthData{Before=()=>{Terminal=true;RequestDraw();Terminal=false;}};partner=new HealthData();
        Require(Execute(new NativeActorNight("observed-terminal"),host,partner)==NativeActorNight.Result.Terminal&&partner.Calls==0&&Draws==0,"one observed terminal cannot be cleared within the native phase");
        Reset();host=new HealthData();partner=new HealthData{Before=()=>Terminal=true};
        Require(Execute(new NativeActorNight("partner-ending"),host,partner)==NativeActorNight.Result.Terminal&&host.Calls==1&&partner.Calls==1&&Draws==0,"partner ending prevents lottery");
        Reset();Terminal=true;host=new HealthData();partner=new HealthData();Execute(new NativeActorNight("already-terminal"),host,partner);
        Require(host.Calls==0&&partner.Calls==0,"preexisting terminal performs no personal changes");

        Reset();host=new HealthData{Before=()=>throw new Exception("health failure")};partner=new HealthData();var fail=new NativeActorNight("fault");
        Throws(()=>Execute(fail,host,partner),"native health exception propagated");
        Require(fail.GetAttempt(5).State==NativeActorNight.AttemptState.Faulted&&Actor==-1&&partner.Calls==0&&Draws==0,"fault restores and stops phases");
        Throws(()=>Execute(fail,host,partner),"fault cannot replay");Require(host.Calls==1,"no duplicate partial health change");
        RequestDraw();Require(Draws==1,"failed scope is cleared");

        Reset();host=new HealthData();partner=new HealthData();var stale=new NativeActorNight("stale");
        Throws(()=>Execute(stale,host,partner,valid:()=>false),"stale phase rejected");Require(host.Calls==0&&stale.GetAttempt(5)==null,"stale request does not reserve/poison the valid night");
        Require(Execute(stale,host,partner)==NativeActorNight.Result.Completed&&host.Calls==1&&partner.Calls==1&&Draws==1,"valid request after stale rejection still executes exactly once");
        Throws(()=>Execute(stale,host,partner,valid:()=>false),"stale request cannot reuse completed receipt as current");
        Reset();host=new HealthData();partner=new HealthData();
        Throws(()=>Execute(new NativeActorNight("no-scope"),host,partner,enter:_=>null),"missing actor restoration token rejected");Require(host.Calls==0,"no naked actor health call");
        Reset();host=new HealthData();partner=new HealthData();
        Throws(()=>Execute(new NativeActorNight("dispose-fault"),host,partner,enter:slot=>{Actor=slot;return new Exit(()=>{Actor=-1;throw new Exception("dispose");});}),"scope disposal fault propagated");
        Require(Actor==-1&&host.Calls==1&&partner.Calls==0&&Draws==0,"disposal fault prevents subsequent effects");

        Reset();host=new HealthData();partner=new HealthData();OnDraw=()=>throw new Exception("draw partial");var drawFail=new NativeActorNight("draw-fault");
        Throws(()=>Execute(drawFail,host,partner),"draw fault propagated");
        Require(drawFail.GetAttempt(5).LotteryAttempted&&!drawFail.GetAttempt(5).LotteryCompleted&&drawFail.GetAttempt(5).CompletedActorMask==3&&Draws==1&&Actor==-1,"partial draw recorded and contexts restored");
        Throws(()=>Execute(drawFail,host,partner),"partial draw never replayed");Require(Draws==1,"draw at most once after exception");
        Reset();host=new HealthData();partner=new HealthData();Eligible=()=>throw new Exception("eligibility");var gateFail=new NativeActorNight("gate-fault");
        Throws(()=>Execute(gateFail,host,partner),"host eligibility failure propagated");Require(Draws==0&&Actor==-1,"eligibility failure no draw");

        Reset();host=new HealthData();partner=new HealthData();host.Before=()=>Throws(()=>Execute(new NativeActorNight("nested"),new HealthData(),new HealthData()),"nested night rejected");Execute(new NativeActorNight("outer"),host,partner);
        Require(Draws==1&&Actor==-1,"outer survives rejected nested night");
        Throws(()=>Execute(new NativeActorNight("alias"),host,host),"same health reference rejected");

        var draw=typeof(LotteryHelper).GetMethod(nameof(LotteryHelper.OnDrawWiningNumber));
        var instruction=new HarmonyLib.CodeInstruction(OpCodes.Call,draw);instruction.labels.Add(default);instruction.blocks.Add(new object());
        var head=new HarmonyLib.CodeInstruction(OpCodes.Nop);var tail=new HarmonyLib.CodeInstruction(OpCodes.Ret);
        var replaced=NativeActorNight.ReplaceLotteryCall(new[]{head,instruction,tail}).ToArray();
        Require(replaced.Length==3&&ReferenceEquals(replaced[0],head)&&ReferenceEquals(replaced[2],tail),"unrelated IL preserved");
        Require(replaced[1].operand.Equals(Request)&&instruction.operand.Equals(draw),"only draw operand cloned/replaced");
        Require(replaced[1].labels.Count==1&&replaced[1].blocks.Count==1&&!ReferenceEquals(replaced[1].labels,instruction.labels),"branch labels and exception metadata preserved");
        Throws(()=>NativeActorNight.ReplaceLotteryCall(new[]{head,tail}).ToArray(),"zero native draw sites rejected");
        Throws(()=>NativeActorNight.ReplaceLotteryCall(new[]{instruction,instruction}).ToArray(),"duplicate native draw sites rejected");
        NativeActorNight.Uninstall(Harmony);Require(!NativeActorNight.IsInstalled,"explicit uninstall");
        Console.WriteLine("PASS "+Checks+" isolated control-flow/IL-transform assertions; stubbed native health/lottery, no game execution.");
    }
}

public sealed class HealthData
{
    public int Calls;public bool Due=true;public Action Before;
    public void OnSleep(){Calls++;Check.Events.Add("health:"+Check.Actor);Before?.Invoke();if(Due)Check.RequestDraw();}
}
public static class LotteryHelper
{
    public static bool IsLotteryUnlocked()=>Check.Eligible();
    public static void OnDrawWiningNumber(){Check.Draws++;Check.Events.Add("draw:"+Check.Actor);Check.OnDraw?.Invoke();}
}

namespace HarmonyLib
{
    // Test-only metadata/patch registration doubles. No runtime IL patch or game assembly is loaded.
    public static class AccessTools{public static MethodInfo Method(Type t,string n)=>t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance);}
    public sealed class HarmonyMethod{internal MethodInfo Method;public HarmonyMethod(MethodInfo m)=>Method=m;}
    public sealed class Patch{public MethodInfo PatchMethod;}
    public sealed class Patches{public List<Patch> Transpilers=new List<Patch>();}
    public sealed class Harmony
    {
        private static Patches installed;
        public static Patches GetPatchInfo(MethodBase m)=>installed;
        public void Patch(MethodBase m,HarmonyMethod transpiler){installed=new Patches();installed.Transpilers.Add(new Patch{PatchMethod=transpiler.Method});}
        public void Unpatch(MethodBase m,MethodInfo p)=>installed=null;
    }
    public sealed class CodeInstruction
    {
        public OpCode opcode;public object operand;public List<Label> labels=new List<Label>();public List<object> blocks=new List<object>();
        public CodeInstruction(OpCode op,object value=null){opcode=op;operand=value;}
        public CodeInstruction(CodeInstruction other){opcode=other.opcode;operand=other.operand;labels=other.labels.ToList();blocks=other.blocks.ToList();}
    }
}
