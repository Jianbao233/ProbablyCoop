using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private static readonly FieldInfo CustomerGrid=AccessTools.Field(typeof(StoreClientInstance),"gridInv");
    private StoreClientInstance replicaCustomer;
    private uint replicaCustomerId;
    private GameObject qaRetiredGrid;
    private int qaRetiredFrame;
    private bool qaCustomerRetained,qaCustomerReleased;

    private void ApplyReplicaCustomer(PlayerStore store,uint id,NativeSnapshotIndex index)
    {
        if((id==0)!=(currentClient==null)) throw new InvalidDataException("Invalid native customer identity");
        if(id!=replicaCustomerId)
        {
            ReleaseReplicaCustomer();
            if(id!=0) replicaCustomer=StoreClientInstance.CreateClientInstance(ReadClient(currentClient,true));
            replicaCustomerId=id;
        }
        else if(replicaCustomer!=null && index.ChangedClient.Count>0)
        {
            var client=replicaCustomer.GetClientBlueprint();
            var grid=NativeBackgroundQa.Enabled ? CustomerGrid.GetValue(replicaCustomer) : null;
            ReadClient(currentClient,true,client,index.ChangedClient);
            if(NativeBackgroundQa.Enabled && !qaCustomerRetained && index.ChangedClient.Contains("itemQueries"))
            {
                if(!ReferenceEquals(grid,CustomerGrid.GetValue(replicaCustomer)) || !ReferenceEquals(client,replicaCustomer.GetClientBlueprint()))
                    throw new InvalidDataException("Guest customer UI objects replaced while updating item queries");
                qaCustomerRetained=true;
                Debug.Log("[probably_coop QA] PASS retained-customer-grid same customer, blueprint and native grid after item query update");
            }
        }
        store.currentClientInstance=replicaCustomer;
    }

    private void ReleaseReplicaCustomer()
    {
        // The vanilla instance constructs its own 10x10 inventory and Unity
        // render tree. Only retire instances created by this guest projection.
        if(replicaCustomer!=null)
        {
            if(PlayerStore.IsInstanceExist() && ReferenceEquals(PlayerStore.Instance.currentClientInstance,replicaCustomer))
                PlayerStore.Instance.currentClientInstance=null;
            if(CustomerGrid.GetValue(replicaCustomer) is GameGridInventory grid && grid.handler?.gameObject!=null)
            {
                if(NativeBackgroundQa.Enabled && !qaCustomerReleased)
                { qaRetiredGrid=grid.handler.gameObject; qaRetiredFrame=Time.frameCount; }
                UnityEngine.Object.Destroy(grid.handler.gameObject);
            }
        }
        replicaCustomer=null; replicaCustomerId=0;
    }

    private void VerifyRetiredGrid()
    {
        if(qaCustomerReleased || ReferenceEquals(qaRetiredGrid,null) || Time.frameCount<=qaRetiredFrame+1) return;
        if(qaRetiredGrid!=null) throw new InvalidDataException("Retired guest customer grid still alive");
        qaCustomerReleased=true; qaRetiredGrid=null;
        Debug.Log("[probably_coop QA] PASS retired-customer-grid previous customer's native render tree released");
    }
}
