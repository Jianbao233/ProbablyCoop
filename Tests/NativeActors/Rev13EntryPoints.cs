using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop;
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
        CheckOpeningCareerRegion(mod);
        CheckOpeningRentAfterLoad(mod);
        CheckCareerMembershipDuringRegisteredStart(mod);
        CheckModExtensionAuthorityScope(mod);
        CheckFrameAttribution(mod);
        CheckOpeningRoomHandoff(mod);
        CheckSharedPerkEventGates(mod);
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
        var expeditionCounters=mod.GetType("ProbablyCoop.NativeExpeditionCounters",true)!;
        var world=mod.GetType("ProbablyCoop.NativeWorld",true)!;
        var nativeBlackscreen = Get(typeof(StoreUIManager), nameof(StoreUIManager.StartBlackscreenSequence));
        foreach (string method in new[] { nameof(MapUIManager.Travel), nameof(MapUIManager.ReturnNow) })
            InOrder(Read(Get(typeof(MapUIManager), method)), nativeBlackscreen);
        var travelLease = Nested(mod.GetType("ProbablyCoop.NativeActorActivities", true)!, "Lease");
        InOrder(Read(Get(world, "ActorTravelRequest")), Get(travelLease, "WrapTravelTransition"));
        var blackscreenPatch = mod.GetType("ProbablyCoop.NativeActorBlackscreenActions", true)!;
        InOrder(Read(Get(blackscreenPatch, "Prefix")), Get(travelLease, "get_IsJointTravel"),
            Get(travelLease, "WrapTravelTransition"));
        var collectLocker=Get(typeof(LouOperativeHelper),nameof(LouOperativeHelper.OnLeaveStore));
        InOrder(Read(Get(typeof(EmporiumEntry),nameof(EmporiumEntry.OnLeaveStore))),collectLocker,
            Get(typeof(EmporiumEntry),nameof(EmporiumEntry.ClearFrontInv)));
        var independentDeparture=Read(Get(world,"DepartIndependent"));
        InOrder(independentDeparture,Get(world,"EnterExpeditionSite"),Get(mod.GetType("ProbablyCoop.NativeActorCarry",true)!,"TransferSelectedBackpack"),collectLocker);
        Check(independentDeparture.Count(i=>i.Calls(collectLocker))==1 &&
            !independentDeparture.Any(i=>i.Calls(Get(typeof(EmporiumEntry),nameof(EmporiumEntry.ClearFrontInv)))),
            "independent departure preserves the original story handoff in the home scope without clearing shared stock");
        InOrder(Read(Get(Nested(night,"NightBoundary"),"Prefix")),Get(expeditionCounters,"CloseParkedMerchant"));
        InOrder(Read(Get(expeditionCounters,"CloseParkedMerchant")),Get(expeditionCounters,"Enter"),
            Get(expeditionCounters,"ClearMerchantStock"),Get(typeof(IDisposable),nameof(IDisposable.Dispose)),
            Get(expeditionCounters,"CloseMerchant"));
        var afterDark=(MethodBase)Get(Nested(night,"TransitionBoundary"),"TargetMethod").Invoke(null,null)!;
        var closeDay=typeof(EndOfDayUIManager).GetMethod(nameof(EndOfDayUIManager.CloseUI),new[]{typeof(bool)})!;
        InOrder(Read(afterDark),closeDay,Get(typeof(PlayerStore),nameof(PlayerStore.EndNight)),
            Get(typeof(PlayerStore),nameof(PlayerStore.BeginDay)),Get(typeof(PlayerStore),nameof(PlayerStore.SaveGame)));
        var personalNight=Transform(Nested(night,"NightBoundary"),"Transpiler",Get(typeof(PlayerStore),nameof(PlayerStore.EndNight)));
        var afterhours=Get(typeof(PlayerStore),nameof(PlayerStore.HandleAfterhourDebuff));
        InOrder(personalNight,Get(typeof(LouOperativeHelper),nameof(LouOperativeHelper.OnNight)),
            afterhours,Get(night,"AgoraphobiaLogs"),Get(night,"SleepBoth"));
        Check(personalNight.Count(i=>i.Calls(afterhours))==1,"shared late-opening report uses the original night entry once");
        var reset=mod.GetType("ProbablyCoop.NativeActorAfterhoursReset",true)!;
        var closeReset=HarmonyMethodExtensions.GetMergedFromType(reset);
        Check(closeReset.declaringType==typeof(StoreShutterButton) && closeReset.methodName==nameof(StoreShutterButton.EndAndClose),
            "partner outing reset follows the original shutter close");
        var outingFlag=typeof(HealthData).GetField(nameof(HealthData.wentToAfterhoursLastNight));
        Check(Read(Get(reset,"Postfix")).Count(i=>i.opcode==OpCodes.Stfld && Equals(i.operand,outingFlag))==2,
            "shutter close clears both personal outing records");
        var outing=mod.GetType("ProbablyCoop.NativeOutingDayRules",true)!;
        var personalOuting=Nested(outing,"PersonalCooldown");
        foreach (string entry in new[]{nameof(StartingPerkContent.GetAgoraphobiaReadyDay),nameof(StartingPerkContent.HandleAgoraphobiaReady)})
            Check(Transform(personalOuting,"Transpiler",Get(typeof(StartingPerkContent),entry))
                .Count(i=>i.Calls(Get(outing,"ReadPersonalDay")))==1, "original agoraphobia uses its own actor history: "+entry);
        Check(Transform(Nested(outing,"SharedDebris"),"Transpiler",Get(typeof(DebrisHelper),nameof(DebrisHelper.OnNight)))
            .Count(i=>i.Calls(Get(outing,"ReadSharedDay")))==1, "native debris work uses the latest outing by either actor");
        var lateOpening = Nested(outing, "SharedLateOpening");
        var latePredicate = Get(outing, "HasLateOpeningPenalty");
        foreach (var route in Targets(lateOpening))
        {
            var native = Read(route);
            var rewrite = Get(lateOpening, "Transpiler");
            var replacement = (IEnumerable<CodeInstruction>)rewrite.Invoke(null, new object[] {native})!;
            Check(replacement.Count(row => row.Calls(latePredicate)) == 1, "one shop-wide penalty check in " + route.Name);
            if (route.DeclaringType == typeof(PlayerStore))
            {
                var withActors = NativeActorFieldAccess.Rewrite(replacement,
                    new[] { new NativeActorFieldAccess.Site(1, 0x04000EDF, false) }, route.Name);
                InOrder(withActors, Get(typeof(NativeActorFieldAccess), "Get_healthData"),
                    latePredicate, Get(typeof(NightLog), nameof(NightLog.AfterhourDebuff)));
            }
        }
        var actors=mod.GetType("ProbablyCoop.NativeActorNight",true)!;
        var sleep=Transform(actors,"ReplaceLotteryCall",Get(typeof(HealthData),nameof(HealthData.OnSleep)));
        Check(sleep.Count(i=>i.Calls(Get(actors,"RequestLottery")))==1 &&
            !sleep.Any(i=>i.Calls(Get(typeof(LotteryHelper),nameof(LotteryHelper.OnDrawWiningNumber)))),
            "native personal night delegates lottery once to the shared coordinator");
        var dayUi=mod.GetType("ProbablyCoop.NativeDayUi",true)!;
        var bellPatch=mod.GetType("ProbablyCoop.GuestBedrollClick",true)!;
        var bedroll=HarmonyMethodExtensions.GetMergedFromType(bellPatch);
        Check(bedroll.declaringType==typeof(StoreBedroll) && bedroll.methodName==nameof(StoreBedroll.OnClick),
            "original bedroll click is routed");
        var panel = mod.GetType("ProbablyCoop.CoopPanel", true)!;
        var room = mod.GetType("ProbablyCoop.RoomSelector", true)!;
        var localRoom = mod.GetType("ProbablyCoop.LocalRoom", true)!;
        var activeJoinInfo = Get(room, "get_GuestJoinInfo");
        var currentMapping = Get(localRoom, "get_GuestMappedAddress");
        Check(Read(activeJoinInfo).Any(row => row.Calls(currentMapping)), "saved room info reads the active transport's mapped endpoint");
        InOrder(Read(Get(panel, "OpenNativeModsFromReview")), activeJoinInfo, Get(panel, "RememberModJoin"), Get(room, "Leave"),
            Get(typeof(MainMenuUIController), nameof(MainMenuUIController.OpenModPanel)));
        InOrder(Read(Get(panel, "OpenWorkshopFromReview")), activeJoinInfo, Get(panel, "RememberModJoin"),
            Get(typeof(ModWorkshop), nameof(ModWorkshop.OpenItemPage)));
        Check(Read(Get(localRoom, "Leave")).Any(row => row.Calls(Get(localRoom, "set_GuestMappedAddress"))),
            "leaving clears mapped join state before reusing the native transport");
        CheckGuestJoinInfo(mod, room, localRoom, activeJoinInfo);
        InOrder(Read(Get(panel, "ResumeModJoin")), Get(panel, "GetSavedModJoin"), Get(panel, "RequestJoin"));
        InOrder(Read(Get(panel, "ObserveJoin")), Get(panel, "GetSavedModJoin"), Get(panel, "ClearSavedModJoin"));
        var review = mod.GetType("ProbablyCoop.NativePartnerJoin", true)!;
        InOrder(Read(Get(review, "Ready")), Get(panel, "RecordModReview"));
        var manifestRow = Nested(mod.GetType("ProbablyCoop.RoomModManifest", true)!, "Row");
        var diagnosticLine = Get(manifestRow, "Diagnostic");
        InOrder(Read(Get(panel, "CopyConnectionDetails")), Get(panel, "ConnectionText"));
        InOrder(Read(Get(panel, "ConnectionText")), Get(panel, "AppendModDiagnostics"));
        InOrder(Read(Get(panel, "AppendModDiagnostics")), diagnosticLine);
        InOrder(Read(Get(panel, "RecordModReview")), diagnosticLine,
            typeof(UnityEngine.Debug).GetMethod(nameof(UnityEngine.Debug.Log),new[] { typeof(object) })!);
        var projectState = Get(world, "ProjectModExtensions");
        var projectionError = mod.GetType("ProbablyCoop.ModExtensionProjectionException", true)!;
        Check(projectState.GetMethodBody()!.ExceptionHandlingClauses.Any(clause =>
                clause.Flags == ExceptionHandlingClauseOptions.Clause && clause.CatchType == typeof(Exception)) &&
            Read(projectState).Any(row => row.opcode == OpCodes.Newobj &&
                row.operand is ConstructorInfo constructor && constructor.DeclaringType == projectionError),
            "mod projection failures capture the mod identifier for join diagnostics");
        InOrder(Read(Get(bellPatch,"Prefix")),Get(world,"RequestBedrollBell"));
        InOrder(Read(Get(world,"Execute")),Get(world,"ReceiveBedrollBell"));
        var ring=Read(Get(world,"ReceiveBedrollBell"));
        var audio=typeof(UnityEngine.AudioSource).GetMethod("PlayOneShot",new[]{typeof(UnityEngine.AudioClip)})!;
        InOrder(ring,Get(world,"ValidOperationContext"),audio);
        Check(!ring.Any(i=>i.Calls(Get(typeof(StoreBell),nameof(StoreBell.RingBell))) ||
            i.Calls(Get(typeof(EndOfDayUIManager),nameof(EndOfDayUIManager.OpenUI)))),
            "bedroll cue does not call a customer or open the host panel");
        var shop=mod.GetType("ProbablyCoop.NativeShop",true)!;
        var outside = Get(shop,"get_AnyPlayerOutside");
        InOrder(Read(Get(shop,"get_CanReloadWorld")),outside);
        InOrder(Read(Get(shop,"CheckNightPlan")),outside,Get(typeof(UnityEngine.GameObject),"get_activeInHierarchy"),
            Get(typeof(PlayerStore),nameof(PlayerStore.CanEndDay)));
        var independentReturn = Read(Get(world,"ReturnIndependent"));
        var arriveStore = Get(typeof(EmporiumEntry),nameof(EmporiumEntry.OnArriveStore));
        InOrder(independentReturn,Get(world,"PresentTravelLocation"),arriveStore,
            Get(typeof(TutorialUIManager),nameof(TutorialUIManager.OnReturnFromAfterhours)));
        int arrival = independentReturn.FindIndex(i=>i.Calls(arriveStore));
        Check(arrival > 0 && independentReturn[arrival-1].LoadsConstant(1L),
            "independent return restores the native shop without spending another visit");
        InOrder(Read(arriveStore),Get(typeof(ModHook),"FireOnReturningStoreEarly"),
            Get(typeof(StorePoster),nameof(StorePoster.LoadStorePoster)),Get(typeof(ModHook),"FireOnReturningStoreLate"));
        InOrder(Read(Get(world,"PresentTravelLocation")),Get(world,"get_IndependentTravel"),
            Get(typeof(PaperUIManager),nameof(PaperUIManager.CloseAllPaperUI)),
            Get(typeof(EmporiumEntry),nameof(EmporiumEntry.CloseItemWindowUIWindow)));
        InOrder(Read(Get(world,"ObserveOperationContext")),Get(world,"ObserveTravelPosition"));
        var blockerParameter = Nested(shop, "NightBlocker").MakeByRefType();
        var startWithReason = shop.GetMethod("StartDay", BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { blockerParameter }, null) ?? throw new MissingMethodException(shop.FullName, "StartDay(out NightBlocker)");
        InOrder(Read(Get(Nested(shop,"PreventDuplicateSettlement"),"Prefix")), startWithReason);
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
        CheckVerifiedHistoryRotation(mod, persistence, checkpoint);
        var save=Transform(persistence,"InsertCapture",Get(typeof(PlayerStore),nameof(PlayerStore.SaveGame)));
        int capture=save.FindIndex(i=>i.Calls(Get(persistence,"CaptureAfterFeatureReset")));
        Check(capture>=2 && save[capture-2].opcode==OpCodes.Stfld &&
            (save[capture-2].operand as FieldInfo)?.Name=="savedItemFeatureList","actor capture is after the native feature reset");
        InOrder(save,Get(persistence,"CaptureAfterFeatureReset"),Get(typeof(PlayerStore),"EncodeSaveItem"),
            Get(typeof(PlayerStore),"WriteSlotFile"));
        var load=Transform(persistence,"ReplaceLoadEnumeration",Get(typeof(PlayerStore),nameof(PlayerStore.LoadGame)));
        InOrder(load,Get(typeof(SaveFiles),nameof(SaveFiles.ResolveLoadFile)),Get(persistence,"ExposeLoadedOpening"),
            Get(typeof(StoreService),nameof(StoreService.HealIntroducedAfterLoad)),Get(typeof(ModHook),"FireOnGameLoadedEarly"),
            Get(typeof(StartingPerk),nameof(StartingPerk.IsMinimalist)),Get(typeof(EmporiumEntry),nameof(EmporiumEntry.SetSmallInv)),
            Get(typeof(ModHook),"FireOnGameLoadedNormal"),Get(typeof(PlayerStore),nameof(PlayerStore.DecodeSaveItem)),
            Get(persistence,"HydrateAndGetItems"),Get(typeof(Shotgun),nameof(Shotgun.ReturnIfLostWithPermit)));
        var loadedOpening = Get(persistence, "get_LoadedOpeningStore");
        var nativeOpening = mod.GetType("ProbablyCoop.NativeOpening", true)!;
        InOrder(Read(Get(persistence, "ExposeLoadedOpening")), Get(persistence, "VerifyLoadedCheckpoint"),
            Get(nativeOpening, "RestorePartnerCareerReputation"));
        InOrder(Read(Get(Nested(nativeOpening, "ScenarioMembership"), "Postfix")), loadedOpening,
            Get(nativeOpening, "PartnerCareer"));
        InOrder(Read(Get(nativeOpening, "UsesCareer")), loadedOpening);
        var perkQueries = mod.GetType("ProbablyCoop.NativeActorPerkQueries", true)!;
        InOrder(Read(Get(perkQueries, "TryShared")), loadedOpening, Get(nativeOpening, "SharedPerks"));
        CheckLoadedOpeningBoundary(persistence, nativeOpening, loadedOpening);
        var decoder=Transform(Nested(persistence,"DecodeBoundary"),"Transpiler",Get(typeof(PlayerStore),nameof(PlayerStore.DecodeSaveItem)));
        InOrder(decoder,Get(persistence,"DecodeNodes"),Get(persistence,"DecodedChildren"),
            Get(typeof(GeneralHelper),nameof(GeneralHelper.LoadImportantStoryItemTag)));
        Console.WriteLine($"PASS Rev13 native entry points and prepared load guard: {checks} checks");
        return 0;
    }

    private static void CheckSharedPerkEventGates(Assembly mod)
    {
        var events = mod.GetType("ProbablyCoop.NativeSharedPerkEvents", true)!;
        var sentinel = Get(typeof(StartingPerk), nameof(StartingPerk.IsSentinel));
        var supply = Get(events, "NeedsSentinelSupplies");
        var suppliers = Nested(events, "SentinelSupply");
        foreach (var method in Targets(suppliers))
        {
            var rewritten = Transform(suppliers, "Transpiler", method);
            Check(rewritten.Count(row => row.Calls(supply)) == 1 && !rewritten.Any(row => row.Calls(sentinel)),
                "each sentinel supplier uses a single shared eligibility check");
        }
        var ban = Get(typeof(StartingPerk), nameof(StartingPerk.IsBannedCommissary));
        var sharedBan = Get(events, "ShopBannedFromCommissary");
        var onboarding = Nested(events, "CommissaryOnboarding");
        foreach (var method in Targets(onboarding))
        {
            var rewrite = Get(onboarding, "Transpiler");
            var rewritten = (IEnumerable<CodeInstruction>)rewrite.Invoke(null, new object[] { Read(method), method })!;
            Check(rewritten.Count(row => row.Calls(sharedBan)) == (method.DeclaringType == typeof(PlayerStore) ? 3 : 1) &&
                !rewritten.Any(row => row.Calls(ban)), "shared commissary introduction and supplies check both actors");
        }
        InOrder(Read(Get(typeof(CommissaryLocation), nameof(CommissaryLocation.IsDiscovered))), ban);
        var world = mod.GetType("ProbablyCoop.NativeWorld", true)!;
        InOrder(Read(Get(world, "ExecuteCommissary")), ban, Get(world, "ObserveTravelPosition"));
        var persistence = mod.GetType("ProbablyCoop.NativeActorPersistence", true)!;
        var restore = Get(events, "RestoreMissedCommissaryIntroduction");
        InOrder(Read(Get(Nested(persistence, "LoadBoundary"), "Postfix")), restore);
        InOrder(Read(Get(world, "ExecutePartnerSetup")), restore);
    }

    private static void CheckLoadedOpeningBoundary(Type persistence, Type opening, MethodInfo loadedOpening)
    {
        var attemptType = Nested(persistence, "LoadAttempt");
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var loading = persistence.GetField("loading", Static)!;
        var attempt = RuntimeHelpers.GetUninitializedObject(attemptType);
        var store = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        store.modData = new Dictionary<string, string> { ["probably_coop.opening.partner.v1"] = "brewer_fixture" };
        attemptType.GetField("Store", fields)!.SetValue(attempt, store);
        try
        {
            Check(loadedOpening.Invoke(null, null) == null, "opening facts are hidden outside a cooperative load");
            loading.SetValue(null, attempt);
            Check(loadedOpening.Invoke(null, null) == null, "opening facts are hidden before the native file matches the prepared one");
            attemptType.GetField("OpeningReady", fields)!.SetValue(attempt, true);
            Check(ReferenceEquals(loadedOpening.Invoke(null, null), store), "opening queries see the verified native store before actor hydration");
            var query = Get(Nested(opening, "ScenarioMembership"), "Postfix");
            object[] selected = { "brewer_fixture", false };
            object[] absent = { "different_fixture", false };
            query.Invoke(null, selected);
            query.Invoke(null, absent);
            Check((bool)selected[1] && !(bool)absent[1], "the native Mod scenario query includes the saved partner career");
        }
        finally { loading.SetValue(null, null); }
    }

    private static void CheckOpeningRoomHandoff(Assembly mod)
    {
        var lobby = mod.GetType("ProbablyCoop.CoopOpeningLobby", true)!;
        var draft = Activator.CreateInstance(lobby, nonPublic: true);
        var originalId = Guid.NewGuid();
        FieldInfo Field(string name) => lobby.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        Field("id").SetValue(draft, originalId);
        Field("hostScenario").SetValue(draft, "water_merchant");
        Field("weeks").SetValue(draft, 5);
        Field("selectedScenario").SetValue(draft, "chemist");
        Field("selectedPerks").SetValue(draft, new[] { "working_pipes" });
        Get(lobby, "SuspendHostDraft").Invoke(draft, null);
        Check((Guid)Field("id").GetValue(draft)! != originalId &&
            Field("selectedScenario").GetValue(draft) == null && Field("selectedPerks").GetValue(draft) == null,
            "the old partner draft is retired for the next guest");
        Check((string)Field("hostScenario").GetValue(draft) == "water_merchant" &&
            (int)Field("weeks").GetValue(draft)! == 5, "the host keeps their opening and rent term during the Mod handoff");
    }

    private static void CheckGuestJoinInfo(Assembly mod, Type selectorType, Type localType, MethodInfo readJoinInfo)
    {
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var selector = RuntimeHelpers.GetUninitializedObject(selectorType);
        var local = RuntimeHelpers.GetUninitializedObject(localType);
        var linkType = mod.GetType("ProbablyCoop.Core.LoopbackLink", true)!;
        var link = RuntimeHelpers.GetUninitializedObject(linkType);
        string code = "PSI-127.0.0.1-18042-1234567890abcdef1234567890abcdef";
        linkType.GetField("<Code>k__BackingField", fields)!.SetValue(link, code);
        localType.GetField("link", fields)!.SetValue(local, link);
        selectorType.GetField("active", fields)!.SetValue(selector, local);
        var setMapping = Get(localType, "set_GuestMappedAddress");
        setMapping.Invoke(local, new object[] { "203.0.113.12:28015" });
        Check((string)readJoinInfo.Invoke(selector, null) == "", "the host does not supply a guest resume target");
        localType.GetField("guest", fields)!.SetValue(local, true);
        Check((string)readJoinInfo.Invoke(selector, null) == code + "\n203.0.113.12:28015",
            "a mapped guest resume contains the active room code and the parsed connection endpoint");
        setMapping.Invoke(local, new object[] { null });
        Check((string)readJoinInfo.Invoke(selector, null) == code, "a direct guest resume contains only the active room code");
    }

    private static void CheckOpeningRentAfterLoad(Assembly mod)
    {
        var rent = mod.GetType("ProbablyCoop.OpeningRent", true)!;
        var persistence = mod.GetType("ProbablyCoop.NativeActorPersistence", true)!;
        var loading = persistence.GetField("loading", Static)!;
        var attemptType = Nested(persistence, "LoadAttempt");
        var attempt = RuntimeHelpers.GetUninitializedObject(attemptType);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var store = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        var otherStore = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        var station = (StoreStation)RuntimeHelpers.GetUninitializedObject(typeof(StoreStation));
        var currentStation = typeof(StoreStation).GetField("instance", Static)!;
        var priorStation = currentStation.GetValue(null);
        var priorAttempt = loading.GetValue(null);
        var openingReady = attemptType.GetField("OpeningReady", instance)!;
        attemptType.GetField("Store", instance)!.SetValue(attempt, store);
        const string key = "probably_coop.opening.rent.v1";
        store.modData = new();
        otherStore.modData = store.modData;

        void SetLedger(int[] amounts, int index, int arrears, int lastDay)
        {
            store.modData[key] = new JObject
            {
                ["amounts"] = new JArray(amounts), ["index"] = index,
                ["arrears"] = arrears, ["lastDay"] = lastDay
            }.ToString(Newtonsoft.Json.Formatting.None);
            store.temporaryRent = (index < amounts.Length ? amounts[index] : 0) + arrears;
        }
        var describe = Get(rent, "Describe");
        (int, int) Debt(PlayerStore player) => ((int, int))describe.Invoke(null, new object[] { player })!;
        var prefix = Get(Nested(rent, "WeeklyCharge"), "Prefix");
        (bool Run, object Plan) WeeklyCharge(int day)
        {
            station.dayCounter = day;
            object[] args = { store, null! };
            bool run = (bool)prefix.Invoke(null, args)!;
            return (run, args[1].GetType().GetField("Item1")!.GetValue(args[1])!);
        }

        try
        {
            loading.SetValue(null, attempt);
            currentStation.SetValue(null, station);
            SetLedger(new[] { 0, 0, 0, 0, 0, 1 }, 0, 0, -1);
            Check(Debt(store) == (0, 0), "unverified native load cannot expose a cooperative ledger");
            openingReady.SetValue(attempt, true);
            Check(Debt(store) == (0, 1) && Debt(otherStore) == (0, 0),
                "verified load exposes future installments even before actor attachment, only for its own shop");
            SetLedger(new[] { 1 }, 1, 1, 42);
            Check(Debt(store) == (0, 1) && WeeklyCharge(49) is { Run: true, Plan: not null },
                "a completed schedule can still have unpaid debt to collect");
            SetLedger(new[] { 1 }, 1, 0, 42);
            Check(WeeklyCharge(42) is { Run: false, Plan: null } &&
                WeeklyCharge(43) is { Run: true, Plan: null },
                "the final receipt deduplicates only its own date; later rent is native");
        }
        finally
        {
            currentStation.SetValue(null, priorStation);
            loading.SetValue(null, priorAttempt);
        }
    }

    private static void CheckFrameAttribution(Assembly mod)
    {
        var tracer = mod.GetType("ProbablyCoop.NativeFrameAttribution", false);
        if (tracer == null) return; // The ordinary build has no frame tracing code.

        var drop = mod.GetType("ProbablyCoop.NativeFrameDropAttribution", true)!;
        var target = HarmonyMethodExtensions.GetMergedFromType(drop);
        Check(target.declaringType == typeof(ItemMouseDragHandler) && target.methodName == "EndDrag",
            "QA drop tracing binds to the original drop entry");
        var world = mod.GetType("ProbablyCoop.NativeWorld", true)!;
        var measure = Get(tracer, "Measure");
        var verify = Read(Get(world, "VerifyStockSlice"));
        InOrder(verify, measure, Get(typeof(System.Diagnostics.Stopwatch), "GetTimestamp"));

        var stage = Nested(tracer, "Stage");
        IDisposable Start(string label) => (IDisposable)measure.Invoke(null, new[] { Enum.Parse(stage, label) })!;
        var tracing = tracer.GetField("tracingHost", Static)!;
        var covered = tracer.GetField("mainCoveredTicks", Static)!;
        var totals = (long[])tracer.GetField("MainTicks", Static)!.GetValue(null)!;
        long Ticks(string label) => totals[(int)Enum.Parse(stage, label)];
        using (Start("LocalDrop")) System.Threading.Thread.SpinWait(128);
        Check((long)covered.GetValue(null)! == 0 && Ticks("LocalDrop") == 0,
            "frame measurement stays disabled outside the host trace");
        tracing.SetValue(null, true);
        try
        {
            using (Start("Runtime"))
            using (Start("StockAudit"))
            using (Start("StockAudit"))
                System.Threading.Thread.SpinWait(256);
            long runtime = Ticks("Runtime");
            Check(runtime > 0 && Ticks("StockAudit") > 0 && (long)covered.GetValue(null)! == runtime,
                "overlapping runtime and nested stock audit contribute main-thread time once");
            using (Start("LocalDrop")) System.Threading.Thread.SpinWait(128);
            Check(Ticks("LocalDrop") > 0 && (long)covered.GetValue(null)! == runtime + Ticks("LocalDrop"),
                "a separate drop contributes its own non-overlapping interval");
        }
        finally { tracing.SetValue(null, false); }
    }

    private static void CheckModExtensionAuthorityScope(Assembly mod)
    {
        var world = mod.GetType("ProbablyCoop.NativeWorld", true)!;
        var actorSession = mod.GetType("ProbablyCoop.NativeActorSession", true)!;
        var registration = mod.GetType("ProbablyCoop.Api.CoopExtensionRegistration", true)!;
        var readState = Read(Get(world, "ReadModAuthorityState"));
        InOrder(readState, Get(actorSession, "Enter"), Get(world, "EnterExpeditionSite"),
            Get(registration, "get_ReadState"), Get(typeof(Func<string>), "Invoke"),
            Get(typeof(IDisposable), nameof(IDisposable.Dispose)));
        var contract = mod.GetType("ProbablyCoop.ModExtensionContract", true)!;
        InOrder(Read(Get(world, "RunModAction")), Get(registration, "get_Execute"), Get(world, "ReadModAuthorityState"),
            Get(contract, "VerifyActionResult"), Get(actorSession, "MarkModActionFault"));
        InOrder(Read(Get(actorSession, "MarkModActionFault")), Get(actorSession, "MarkFault"));
        var runtime = mod.GetType("ProbablyCoop.CoopRuntime", true)!;
        InOrder(Read(Get(runtime, "RecordHostFaultRecovery")), Get(actorSession, "get_ExtensionActionFailure"),
            Get(actorSession, "get_VerifiedCheckpoint"));
        var submit = Get(world, "RequestModAction");
        InOrder(Read(submit), Get(world, "RunModAction"), Get(world, "NotifyModAction"));
        Check(submit.GetMethodBody()!.ExceptionHandlingClauses.Any(clause =>
            clause.Flags == ExceptionHandlingClauseOptions.Clause && clause.CatchType == typeof(Exception)),
            "a faulted host action can complete its UI callback after the cooperative fault is registered");
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
        var digest=System.Security.Cryptography.SHA256.HashData(original);
        var proof=proofType.GetConstructors(instance).Single().Invoke(new object[]{file,digest,envelope});
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
    private static void CheckOpeningCareerRegion(Assembly mod)
    {
        var opening = mod.GetType("ProbablyCoop.NativeOpening", true)!;
        var getter = Get(opening, "CareerOnly");
        var original = typeof(NewGameData).GetMethod(nameof(NewGameData.HandleInitialItem))!;
        var source = PatchProcessor.GetOriginalInstructions(original, out var generator);
        var rewrite = Get(Nested(opening, "NativeItems"), "Transpiler");
        var code = ((IEnumerable<CodeInstruction>)rewrite.Invoke(null, new object[] { source, generator })!).ToList();
        var replayGates = code.Select((row, index) => (row, index)).Where(pair => pair.row.Calls(getter)).ToArray();
        Check(replayGates.Length == 2, "only two replay guards on the native career region");
        var entryGate = replayGates[0].index;
        var exitGate = replayGates[1].index;
        var careerStart = (Label)code[entryGate + 1].operand;
        var returnTarget = (Label)code[exitGate + 1].operand;
        int careerIndex = code.FindIndex(i => i.labels.Contains(careerStart));
        int returnIndex = code.FindIndex(i => i.labels.Contains(returnTarget));
        var blackscreen = Get(typeof(StoreUIManager), "FinishBlackscreenSequence");
        int sharedIndex = code.FindIndex(i => i.Calls(blackscreen));
        Check(entryGate < sharedIndex && sharedIndex < careerIndex && careerIndex < exitGate && returnIndex == code.Count - 1,
            "secondary career jumps past common setup and to the original return");
        var customCareer = Get(opening, "ApplyRegisteredCareer");
        Check(code[careerIndex].Calls(customCareer) && code[careerIndex + 1].opcode == OpCodes.Brtrue &&
            code[exitGate].labels.Contains((Label)code[careerIndex + 1].operand),
            "custom careers run inside the native career region and skip its modded error swallowing");
        InOrder(code, customCareer, Get(opening, "ApplySecondCareer"),
            Get(typeof(StartingPerk), nameof(StartingPerk.IsSentinel)), Get(typeof(ModHook), "FireOnNewGameStarted"));
        Check(code.Count(i => i.Calls(Get(typeof(ModHook), "FireOnNewGameStarted"))) == 1,
            "shared native opening hook exists once");
        var mentor = Read(Get(typeof(StoreClientListStory), nameof(StoreClientListStory.CreateMentorDisplayCase)));
        var nextLine = Get(typeof(Dialogue), nameof(Dialogue.NextDialogue));
        var setReward = Get(typeof(Dialogue), nameof(Dialogue.SetEndAction));
        int mentorReward = mentor.FindIndex(row => row.Calls(setReward));
        Check(mentorReward >= 0 && mentor.Take(mentorReward).Count(row => row.Calls(nextLine)) == 2 &&
            mentor.Count(row => row.Calls(setReward)) == 2,
            "native showcase reward still belongs to the third line before the separate dismiss callback");
        var cartelGate = Nested(opening, "CartelCareerGate");
        foreach (var entry in Targets(cartelGate))
        {
            var cartel = Transform(cartelGate, "Transpiler", entry);
            InOrder(cartel, typeof(PlayerStore).GetMethod(nameof(PlayerStore.IsStartType))!);
        }
    }

    private static void CheckVerifiedHistoryRotation(Assembly mod, Type persistence, Type checkpoint)
    {
        var patch = Nested(persistence, "PreserveVerifiedHistory");
        var prior = Get(patch, "IncludePrevious");
        var original = Get(typeof(SaveFiles), nameof(SaveFiles.RotateIntoHistory));
        var nativeLimit = typeof(EmporiumOption).GetProperty(nameof(EmporiumOption.SaveHistoryDays))!.GetMethod!;
        var trim = Get(typeof(SaveFiles), "TrimHistory");
        var code = Transform(patch, "Transpiler", original);
        var index = code.FindIndex(row => row.Calls(prior));
        Check(index >= 2 && code[index - 2].Calls(nativeLimit) && code[index - 1].opcode == OpCodes.Ldarg_0 &&
            code[index + 1].LoadsConstant(1L) && code[index + 2].opcode == OpCodes.Sub && code[index + 3].Calls(trim),
            "the verified checkpoint limit is applied only to the native rotation trim for its own slot");

        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const int slot = 12;
        var store = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        store.saveSlotId = slot;
        var sessionType = mod.GetType("ProbablyCoop.NativeActorSession", true)!;
        var session = RuntimeHelpers.GetUninitializedObject(sessionType);
        sessionType.GetField("<Store>k__BackingField", fields)!.SetValue(session, store);
        sessionType.GetField("<NativeSaveSlot>k__BackingField", fields)!.SetValue(session, slot);
        var attempt = Activator.CreateInstance(Nested(persistence, "SaveAttempt"), fields, null, new[] { session }, null);
        var envelope = Activator.CreateInstance(Nested(checkpoint, "Envelope"), fields, null,
            new object[] { Guid.NewGuid(), slot, 2, Guid.NewGuid(), new JObject() }, null);
        var saving = persistence.GetField("saving", Static)!;
        var verified = sessionType.GetProperty("VerifiedCheckpoint", fields)!;
        var previous = saving.GetValue(null);
        int Limit(int value, int forSlot) => (int)prior.Invoke(null, new object[] { value, forSlot })!;
        try
        {
            Check(Limit(1, slot) == 1, "ordinary saves use the selected one-day history limit");
            saving.SetValue(null, attempt);
            Check(Limit(1, slot) == 1, "initial cooperative save has no verified predecessor");
            verified.SetValue(session, envelope);
            Check(Limit(1, slot) == 2 && Limit(3, slot) == 3,
                "cooperative save retains a verified predecessor and respects a larger limit");
            Check(Limit(1, slot + 1) == 1, "a cooperative write cannot change another slot's rotation limit");
            store.saveSlotId = slot + 1;
            Check(Limit(1, slot) == 1, "rotating an old slot after a native slot change keeps the original limit");
        }
        finally { saving.SetValue(null, previous); }
    }

    private static void CheckCareerMembershipDuringRegisteredStart(Assembly mod)
    {
        var openingType = mod.GetType("ProbablyCoop.CoopOpening", true)!;
        var nativeOpening = mod.GetType("ProbablyCoop.NativeOpening", true)!;
        var run = mod.GetType("ProbablyCoop.CoopRun", true)!;
        var careerType = mod.GetType("ProbablyCoop.Api.CoopOpeningCareer", true)!;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var pending = nativeOpening.GetField("pending", Static)!;
        var armed = run.GetProperty("Armed", Static)!;
        var currentRequest = typeof(NewGameData).GetProperty(nameof(NewGameData.Instance), Static)!;
        var currentStore = typeof(PlayerStore).GetField("instance", Static)!;
        var request = (NewGameData)RuntimeHelpers.GetUninitializedObject(typeof(NewGameData));
        var store = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        var detachedStore = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        var opening = RuntimeHelpers.GetUninitializedObject(openingType);
        var query = Get(nativeOpening, "UsesCareer");
        bool Includes(PlayerStore target, NewGameData.StartType type) =>
            (bool)query.Invoke(null, new object[] { target, type })!;

        var host = new StartingScenario { id = "water_merchant", startType = NewGameData.StartType.WATER_MERCHANT };
        var partner = new StartingScenario { id = "test_brewer", startType = NewGameData.StartType.MODDED, source = "test_provider" };
        var savedPending = pending.GetValue(null);
        var savedArmed = armed.GetValue(null);
        var savedRequest = currentRequest.GetValue(null);
        var savedStore = currentStore.GetValue(null);
        store.startType = host.startType;
        store.startScenarioId = host.id;
        openingType.GetField("Request", fields)!.SetValue(opening, request);
        openingType.GetField("HostScenario", fields)!.SetValue(opening, host);
        openingType.GetField("PartnerScenario", fields)!.SetValue(opening, partner);
        openingType.GetField("partnerCareer", fields)!.SetValue(opening,
            Activator.CreateInstance(careerType, new object[] { partner.id, 0, null }));
        partner.onStart = () =>
        {
            Check(store.startScenarioId == partner.id, "registered opening callback sees its own native scenario ID");
            Check(Includes(store, host.startType) && Includes(store, partner.startType),
                "registered partner opening callback can query both career types");
            Check(!Includes(detachedStore, host.startType) && !Includes(store, NewGameData.StartType.FENCE),
                "paired opening membership is limited to the current shop and the selected careers");
        };

        var careerOnly = nativeOpening.GetField("careerOnly", Static)!;
        var savedCareerOnly = careerOnly.GetValue(null);
        try
        {
            pending.SetValue(null, opening);
            armed.SetValue(null, true);
            currentRequest.SetValue(null, request);
            currentStore.SetValue(null, store);
            careerOnly.SetValue(null, true);
            Check((bool)Get(nativeOpening, "ApplyRegisteredCareer").Invoke(null, null)!, "registered guest career was initialized");
            Check(store.startType == host.startType && store.startScenarioId == host.id,
                "registered guest callback restores the primary native career");
        }
        finally
        {
            careerOnly.SetValue(null, savedCareerOnly);
            pending.SetValue(null, savedPending);
            armed.SetValue(null, savedArmed);
            currentRequest.SetValue(null, savedRequest);
            currentStore.SetValue(null, savedStore);
        }
    }

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
