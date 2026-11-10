using System;
using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly int soakSeconds;
    private bool stockVerified,soakDone,soakWaiting,soakFinishing;
    private double soakStart,soakNext,soakSent;
    private ulong soakRevision;
    private double idleStart,idleChangeAt,idleLongest;
    private float idleRunSeconds;
    private ulong idleFirstRevision,idleRevision;
    private ReplicaLink.Stamp soakStamp;
    private int soakDay,soakMode,soakCommits,soakLastMinute;
    private GameItemElement soakMachine,soakWater;
    private readonly bool repairRecords=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-repair-records")>=0;
    private ulong repairFrom;
    private double repairAt;
    private bool repairVerified;
    private readonly byte[][] samplePackets=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-snapshot-samples")>=0 ? new byte[3][] : null;
    private readonly ulong[] sampleRevisions=new ulong[3];
    private readonly byte[][] sampleBases=new byte[3][];
    private bool RememberPacket(ReplicaLink link,int index)
    {
        if(samplePackets==null || index>=samplePackets.Length) return true;
        if(link.State==null || link.Revision!=NativeWorld.Guest.QaAppliedRevision ||
            index>0 && link.Revision<=sampleRevisions[index-1]) return false;
        // ReplicaLink only publishes complete immutable buffers. Keep these
        // exact applied payloads and attribute their one disk export to QA.
        samplePackets[index]=link.State; sampleRevisions[index]=link.Revision;
        if(NativeRecordFrame.IsDelta(link.State))
        {
            Require(link.FullState!=null && NativeRecordFrame.HasBase(link.State,link.FullRevision),"sample lost its native baseline");
            sampleBases[index]=link.FullState;
        }
        if(index==samplePackets.Length-1)
        {
            using var timing=NativePerformance.Measure("qa.sample_export",NativePerformance.FrameWork.QaRender);
            for(int i=0;i<samplePackets.Length;i++)
            {
                Require(samplePackets[i]!=null,"missing applied native sample "+i);
                ModHelper.WriteModDataFileBytes("probably_coop","qa-native-sample-"+i+".bin",samplePackets[i]);
                if(sampleBases[i]!=null)
                    ModHelper.WriteModDataFileBytes("probably_coop","qa-native-sample-"+i+"-basis.bin",sampleBases[i]);
                sampleBases[i]=null;
                samplePackets[i]=null;
            }
            Record("PASS native-snapshot-samples","base and two original radio changes; applied revisions="+string.Join(",",sampleRevisions));
        }
        return true;
    }
    private bool RunSustainedOperations(ReplicaLink link,double now)
    {
        if(soakSeconds==0 || soakDone) return true;
        if(soakMachine==null)
        {
            if(!RememberPacket(link,0)) return false;
            CheckDenseStock();
            soakMachine=(GameItemElement)MachineReplica();
            soakWater=NativeWorld.Guest.Objects.Values.OfType<GameItemElement>().Single(i=>i.identifier=="large_bottled_water");
            soakStart=now; soakRevision=link.Revision; soakStamp=link.CaptureStamp(); soakDay=StoreStation.Instance.dayCounter;
            ItemMouseDoubleClickHandler.current.OpenContentAction(soakMachine,new Vector2(200,250));
            Record("BEGIN sustained-native-operating","seconds="+soakSeconds+"; original machine radio inputs and independent item reads");
        }
        Require(soakStamp.IsCurrent(link) && PlayerStore.Instance.saveSlotId==-1 && StoreStation.Instance.dayCounter==soakDay &&
            soakMachine.contentWindow.IsVisible(),"sustained native connection, profile, day or local machine window changed");
        int elapsed=(int)(now-soakStart);
        if(idleStart!=0) return ObserveIdleClock(link,now);
        SampleDenseProjection();
        var reading=elapsed%4<2 ? soakMachine : soakWater;
        var info=elapsed%2==0 ? reading.GetTooltip() : reading.GetTooltipAdvanced();
        Require(!string.IsNullOrEmpty(info.nakedText),"sustained native read empty");
        if(soakWaiting)
        {
            Require(now-soakSent<12,"sustained machine action timed out");
            if(QaWaiting(link)) return false;
            Require(QaResult(link)==(ushort)ShopResult.Applied,"sustained native control rejected: "+QaResult(link));
            if(soakMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=soakMode) return false;
            if(!RememberPacket(link,soakCommits+1)) return false;
            soakCommits++; soakWaiting=false;
            if(soakCommits==1) Record("TRACE sustained-first-commit","original machine radio applied during the live reading workload");
            if(denseCount!=0 && soakCommits==1)
                NativeOffscreenCapture.Capture("qa-dense-production");
        }
        if(repairRecords && !repairVerified && soakCommits>=3 && !ProbeRecordRepair(link,now)) return false;
        if(elapsed/60>soakLastMinute)
        {
            soakLastMinute=elapsed/60;
            Record("TRACE sustained-native-minute",$"elapsed={elapsed} confirmed={soakCommits} revision={link.Revision}");
        }
        if(soakFinishing)
        {
            CheckDenseStock();
            ReportDenseProjection();
            if(denseCount!=0) Record("PASS dense-native-stock","count="+denseCount+" still present after original production and item reads");
            Require(link.Revision>soakRevision && soakCommits>=Math.Max(1,soakSeconds/8),"sustained native operations too few");
            Record("PASS sustained-native-operating",$"continuous={elapsed}s confirmedRadioChanges={soakCommits} revision={soakRevision}->{link.Revision}; slot=-1, same epoch and original local window");
            idleStart=idleChangeAt=now; idleFirstRevision=idleRevision=link.Revision;
            idleRunSeconds=PlayerStore.Instance.GetCurrentRunPlayTime();
            return false;
        }
        if(elapsed>=soakSeconds)
        {
            soakFinishing=true;
            if(soakMode==0) return false;
            soakMode=0;
        }
        else
        {
            if(now<soakNext) return false;
            soakMode=soakMode==1 ? 0 : 1;
        }
        var list=Modes(soakMachine);
        int oldRule=soakMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt();
        list.Get(soakMode).SetOn(true,true);
        Require(QaWaiting(link) && list.selected==soakMode && soakMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()==oldRule,
            "native radio light did not respond locally or ran the rule before the host");
        if(soakCommits==0)
        {
            ulong serial=QaIssued(link);
            int intermediate=soakMode==1 ? 0 : 1;
            list.Get(intermediate).SetOn(true,true);
            Require(QaIssued(link)==serial && list.selected==intermediate,"second click did not preview independently of pending request");
            list.Get(soakMode).SetOn(true,true);
            Require(QaIssued(link)==serial && list.selected==soakMode,"latest local click was not coalesced");
            Record("PASS local-machine-feedback","native radio light followed each input immediately; three clicks coalesced while heater state waited for host");
            if(denseCount!=0) NativeOffscreenCapture.Capture("qa-radio-immediate");
        }
        soakWaiting=true; soakSent=now; soakNext=now+5;
        return false;
    }
    private bool ProbeRecordRepair(ReplicaLink link,double now)
    {
        if(repairFrom==0)
        {
            ulong basis=link.FullRevision;
            if(!link.RequestFull(basis)) return false;
            repairFrom=basis; repairAt=now;
            Record("TRACE native-record-repair","guest discarded checkpoint "+basis+" after an applied radio change");
            return false;
        }
        Require(now-repairAt<8,"full record recovery timed out");
        if(link.FullRevision<=repairFrom || NativeWorld.Guest.QaAppliedRevision<link.FullRevision) return false;
        Require(NativeWorld.Guest.AuditProjection(out string detail),"repaired native inventory differs: "+detail);
        Require(soakMachine.contentWindow.IsVisible() && soakMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()==soakMode,
            "checkpoint repair reset original machine window or radio choice");
        Record("PASS native-record-repair",FormattableString.Invariant($"old={repairFrom} replacement={link.FullRevision} seconds={now-repairAt:F3}; {detail}"));
        repairVerified=true; return true;
    }
    private bool ObserveIdleClock(ReplicaLink link,double now)
    {
        idleLongest=Math.Max(idleLongest,now-idleChangeAt);
        if(idleRevision!=link.Revision) { idleRevision=link.Revision; idleChangeAt=now; }
        if(now-idleStart<8) return false;
        double elapsed=now-idleStart;
        float clock=PlayerStore.Instance.GetCurrentRunPlayTime()-idleRunSeconds;
        Require(Math.Abs(clock-elapsed)<0.75 && idleLongest>0.5,
            $"native run clock or idle interval differed: clock={clock} elapsed={elapsed} longest={idleLongest} revisions={idleRevision-idleFirstRevision}");
        Record("PASS native-idle-clock",FormattableString.Invariant($"seconds={elapsed:F2} nativeClockAdvance={clock:F2} worldRevisions={idleRevision-idleFirstRevision} longestWithoutRevision={idleLongest:F2}; original machine window remained open"));
        soakMachine.contentWindow.Hide(); soakDone=true;
        return true;
    }
}
