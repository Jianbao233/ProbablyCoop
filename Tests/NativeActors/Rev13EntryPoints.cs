using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Steamworks;

// Reads the real installed IL, runs our instruction rewrites and checks the detached load proof.
// No Harmony installation, original gameplay calls, Unity, or native save I/O.
internal static class Rev13EntryPoints
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string[] args)
    {
        if(args.Length!=1 || !File.Exists(args[0]))
            throw new ArgumentException("Pass the freshly built ProbablyCoop DLL.");
        var mod=Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Check(typeof(PlayerStore).GetField("VERSION_NUMBER")?.GetRawConstantValue() as string == "VERSION 100-Rev13",
            "installed game must be Rev13");
        CheckSteamJoinArguments(mod);
        InOrder(Read(Get(typeof(InputActionManager),"Update")),
            Get(typeof(InputActionHandler),nameof(InputActionHandler.UpdateRelease)),
            Get(typeof(InputActionHandler),nameof(InputActionHandler.UpdateMouseMove)));
        InOrder(Read(Get(typeof(ItemMouseDragHandler),"OnEventRelease")),
            Get(typeof(ItemMouseDragHandler),"EndDrag"),Get(typeof(ItemMouseDragHandler),"OnEventReset").GetBaseDefinition());
        InOrder(Read(Get(typeof(GameItemElement),"ValidateSelectLocked")),
            typeof(GameItemElement).GetMethod(nameof(GameItemElement.ToggleFakeBackground),new[] { typeof(bool),typeof(float),typeof(float) })!,
            Get(typeof(GameItemElement),"UpdateRectTransform"));
        var trade=mod.GetType("ProbablyCoop.NativeTradeRequests",true)!;
        Check(Targets(trade).Select(method=>method.Name).Order().SequenceEqual(
            new[] {"OnBuyAllClick","OnOfferAcceptClick","OnSellAllClick","OnWholesaleClick"}.Order()),
            "the four original tablet actions are routed");
        var stash=typeof(PlayerStore).GetMethod(nameof(PlayerStore.MoveDealItemToInventory))!;
        var stashPatch=mod.GetType("ProbablyCoop.NativeDealStash",true)!;
        var descriptor=HarmonyMethodExtensions.GetMergedFromType(stashPatch);
        Check(descriptor.declaringType==typeof(PlayerStore) && descriptor.methodName==stash.Name &&
            stashPatch.GetMethod("Finalizer",Static)!=null,"actual optional stash sink has a bounded Harmony scope");
        foreach(var from in new[] {
            typeof(PlayerStore).GetMethod(nameof(PlayerStore.BuyItem))!,
            typeof(PlayerStore).GetMethod(nameof(PlayerStore.AcceptBarter))!,
            typeof(NegociationUIManager).GetMethod(nameof(NegociationUIManager.OnWholesaleClick))!
        }) InOrder(Read(from),stash);
        InOrder(Read(stash),Get(typeof(EmporiumOption),"get_MoveBoughtItemsToInventory"),
            Get(typeof(GraphUtils),nameof(GraphUtils.TryAcceptAll)));
        InOrder(Read(Get(typeof(GraphUtils),nameof(GraphUtils.TryAcceptAll))),Get(typeof(SlotMarker),nameof(SlotMarker.TryAcceptOnce)));
        var slotGuard=Get(mod.GetType("ProbablyCoop.NativeHandTransferInput",true)!,"BlocksHostSlot");
        foreach(string patch in new[]{"ReplicaNativeDrop","ReplicaUncheckedDrop"})
            InOrder(Read(Get(mod.GetType("ProbablyCoop."+patch,true)!,"Prefix")),slotGuard);

        var night=mod.GetType("ProbablyCoop.NativeActorNightIntegration",true)!;
        var afterDark=(MethodBase)Get(Nested(night,"TransitionBoundary"),"TargetMethod").Invoke(null,null)!;
        var closeDay=typeof(EndOfDayUIManager).GetMethod(nameof(EndOfDayUIManager.CloseUI),new[]{typeof(bool)})!;
        InOrder(Read(afterDark),closeDay,Get(typeof(PlayerStore),nameof(PlayerStore.EndNight)),
            Get(typeof(PlayerStore),nameof(PlayerStore.BeginDay)),Get(typeof(PlayerStore),nameof(PlayerStore.SaveGame)));
        var personalNight=Transform(Nested(night,"NightBoundary"),"Transpiler",Get(typeof(PlayerStore),nameof(PlayerStore.EndNight)));
        InOrder(personalNight,Get(typeof(LouOperativeHelper),nameof(LouOperativeHelper.OnNight)),
            Get(night,"AfterhourLogs"),Get(night,"AgoraphobiaLogs"),Get(night,"SleepBoth"));
        var actors=mod.GetType("ProbablyCoop.NativeActorNight",true)!;
        var sleep=Transform(actors,"ReplaceLotteryCall",Get(typeof(HealthData),nameof(HealthData.OnSleep)));
        Check(sleep.Count(i=>i.Calls(Get(actors,"RequestLottery")))==1 &&
            !sleep.Any(i=>i.Calls(Get(typeof(LotteryHelper),nameof(LotteryHelper.OnDrawWiningNumber)))),
            "native personal night delegates lottery once to the shared coordinator");
        var dayUi=mod.GetType("ProbablyCoop.NativeDayUi",true)!;
        InOrder(Read(Get(dayUi,"ShowGuestPlan")),Get(typeof(PaperUIManager),nameof(PaperUIManager.CloseAllPaperUIForSleep)),
            Get(typeof(EndOfDayUIManager),nameof(EndOfDayUIManager.OpenUI)));
        InOrder(Read(Get(dayUi,"Tick")),closeDay,Get(typeof(PaperUIManager),nameof(PaperUIManager.RestorePaperUIClosedForSleep)),
            Get(typeof(StartOfDayUIManager),nameof(StartOfDayUIManager.ShowMorningReport)));

        var persistence=mod.GetType("ProbablyCoop.NativeActorPersistence",true)!;
        var registry=mod.GetType("ProbablyCoop.CoopRun",true)!;
        var checkpoint=mod.GetType("ProbablyCoop.NativeActorCheckpoint",true)!;
        InOrder(Read(Get(Nested(persistence,"LoadBoundary"),"Prefix")),Get(registry,"TakePreparedResume"),
            Get(registry,"RegisteredRunsAtSlot"),Get(persistence,"ReadLoadEnvelope"));
        InOrder(Read(Get(persistence,"ReadLoadEnvelope")),Get(persistence,"ReadFileImage"),
            Get(Nested(registry,"PreparedResume"),"Match"),Get(checkpoint,"ReadReceipt"),Get(checkpoint,"DecodeReceipt"));
        CheckPreparedResume(registry,checkpoint);
        var save=Transform(persistence,"InsertCapture",Get(typeof(PlayerStore),nameof(PlayerStore.SaveGame)));
        int capture=save.FindIndex(i=>i.Calls(Get(persistence,"CaptureAfterFeatureReset")));
        Check(capture>=2 && save[capture-2].opcode==OpCodes.Stfld &&
            (save[capture-2].operand as FieldInfo)?.Name=="savedItemFeatureList","actor capture is after the native feature reset");
        InOrder(save,Get(persistence,"CaptureAfterFeatureReset"),Get(typeof(PlayerStore),"EncodeSaveItem"),
            Get(typeof(PlayerStore),"WriteSlotFile"));
        var load=Transform(persistence,"ReplaceLoadEnumeration",Get(typeof(PlayerStore),nameof(PlayerStore.LoadGame)));
        InOrder(load,Get(typeof(SaveFiles),nameof(SaveFiles.ResolveLoadFile)),Get(typeof(PlayerStore),nameof(PlayerStore.DecodeSaveItem)),
            Get(persistence,"HydrateAndGetItems"),Get(typeof(Shotgun),nameof(Shotgun.ReturnIfLostWithPermit)));
        var decoder=Transform(Nested(persistence,"DecodeBoundary"),"Transpiler",Get(typeof(PlayerStore),nameof(PlayerStore.DecodeSaveItem)));
        InOrder(decoder,Get(persistence,"DecodeNodes"),Get(persistence,"DecodedChildren"),
            Get(typeof(GeneralHelper),nameof(GeneralHelper.LoadImportantStoryItemTag)));
        Console.WriteLine($"PASS Rev13 native entry points and prepared load guard: {checks} checks");
        return 0;
    }

    private static void CheckSteamJoinArguments(Assembly mod)
    {
        var inbox = mod.GetType("ProbablyCoop.SteamJoinInbox", true)!;
        var parser = Get(inbox, "TryReadArguments");
        var lobby = new CSteamID(new AccountID_t(1), Constants.k_unSteamAccountInstanceMask & 0x40000,
            EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeChat);
        var connect = (string)Get(inbox, "ConnectString").Invoke(null, new object[] { lobby })!;
        var arguments = connect.Split(' ');
        bool Parse(string[] args)
        {
            object[] call = { args, CSteamID.Nil };
            return (bool)parser.Invoke(null, call)! && (CSteamID)call[1] == lobby;
        }
        Check(Parse(arguments), "Steam friend connect data round-trips");
        Check(Parse(new[] { "Probably Stolen.exe", "+connect_lobby", arguments[1] }), "Steam lobby launch argument");
        Check(!Parse(new[] { "+connect_lobby", "0" }), "nil Steam lobby rejected");
        var individual = new CSteamID(new AccountID_t(1), 1,
            EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeIndividual);
        Check(!Parse(new[] { "+connect_lobby", individual.ToString() }), "Steam user ID rejected");
        Check(!Parse(new[] { "+connect_lobby" }), "missing Steam lobby rejected");
        Check(!Parse(arguments.Concat(arguments).ToArray()), "ambiguous Steam connect data rejected");
    }

    private static void CheckPreparedResume(Type registry,Type checkpoint)
    {
        const BindingFlags instance=BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var run=Guid.NewGuid();
        var envelope=Nested(checkpoint,"Envelope").GetConstructors(instance).Single().Invoke(
            new object[]{run,9,6,Guid.NewGuid(),new JObject()});
        var proofType=Nested(registry,"PreparedResume");
        string file=Path.Combine(Path.GetTempPath(),"save_9.es3");
        var original=new byte[]{13,27,37};
        var proof=proofType.GetConstructors(instance).Single().Invoke(new object[]{file,original,envelope});
        var match=Get(proofType,"Match");
        var registered=new HashSet<Guid>{run};
        object Match(string path,int slot,HashSet<Guid> runs,byte[] bytes) =>
            match.Invoke(proof,new object[]{path,slot,runs,bytes});
        Check(ReferenceEquals(envelope,Match(file,9,registered,original.ToArray())),"an independently read identical image reuses the prepared proof");
        Check(Match(file,9,registered,new byte[]{13,27,38})==null,"same-size replacement re-enters full validation");
        Check(Match(file+".old",9,registered,original)==null,"a different file cannot reuse the proof");
        Check(Match(file,8,registered,original)==null,"a different slot cannot reuse the proof");
        Check(Match(file,9,new HashSet<Guid>(),original)==null,"a removed registration cannot reuse the proof");
    }

    private static Type Nested(Type type,string name) => type.GetNestedType(name,BindingFlags.NonPublic)
        ?? throw new MissingMemberException(type.FullName,name);
    private static MethodInfo Get(Type type,string name) => type.GetMethod(name,Static | BindingFlags.Instance)
        ?? throw new MissingMethodException(type.FullName,name);
    private static IEnumerable<MethodBase> Targets(Type type) =>
        (IEnumerable<MethodBase>)Get(type,"TargetMethods").Invoke(null,null)!;
    private static List<CodeInstruction> Read(MethodBase method) => PatchProcessor.GetOriginalInstructions(method);
    private static List<CodeInstruction> Transform(Type type,string method,MethodBase original) =>
        ((IEnumerable<CodeInstruction>)Get(type,method).Invoke(null,new object[]{Read(original)})!).ToList();
    private static void InOrder(List<CodeInstruction> code,params MethodInfo[] methods)
    {
        int position=-1;
        foreach(var method in methods)
        {
            position=code.FindIndex(position+1,i=>i.Calls(method));
            Check(position>=0,$"missing or out-of-order IL call {method.DeclaringType?.Name}.{method.Name}");
        }
    }
    private static void Check(bool condition,string description)
    {
        if(!condition) throw new InvalidOperationException(description);
        checks++;
    }
}
