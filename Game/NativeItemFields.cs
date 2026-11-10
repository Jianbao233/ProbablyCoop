using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

internal static partial class NativeMirrorProbe
{
    private static JObject CaptureItemFields(GameItem item)
    {
        if(Context?.Deferred==null) return FieldData(item,ItemFields);
        using var timing=NativePerformance.Measure("host.freeze_item_fields");
        return Context.Deferred.HasItemFieldCache
            ? Context.Deferred.CaptureCachedFields(item,ItemFields,ItemReaders.Immutable)
            : Context.Deferred.Capture(item,ItemFields,ItemReaders.Freeze);
    }

    // Use backing fields from the existing reflection contract, including for
    // virtual names. Reading a property could invoke an item's custom getter.
    private static class ItemReaders
    {
        internal static readonly Action<GameItem,object[]> Freeze=Read;
        internal static readonly Func<GameItem,object[],object[]> Immutable=ReadImmutable;
        private static readonly Func<GameItem,object,object>[] Readers=Array.ConvertAll(ItemFields,Reader);
        private static int audited,typed;
        private static long typedTicks,originalTicks;
        private static Func<GameItem,object,object> Reader(FieldInfo field)
        {
            try
            {
                if(field.FieldType==typeof(string)) return Create<string>(field);
                if(field.FieldType==typeof(int)) return Create<int>(field);
                if(field.FieldType==typeof(long)) return Create<long>(field);
                if(field.FieldType==typeof(bool)) return Create<bool>(field);
            }
            catch(Exception error) when(error is ArgumentException or NotSupportedException or MemberAccessException)
            {
                // Reflection is the original path for unsupported runtimes.
            }
            return (item,previous)=>field.GetValue(item);
        }
        private static Func<GameItem,object,object> Create<T>(FieldInfo field)
        {
            var read=AccessTools.FieldRefAccess<GameItem,T>(field);
            typed++;
            return (item,previous)=>{
                T current=read(item);
                if(previous is T prior && EqualityComparer<T>.Default.Equals(current,prior)) return previous;
                return current;
            };
        }
        private static void Copy(GameItem item,object[] row)
        {
            for(int i=0;i<Readers.Length;i++) row[i]=Readers[i](item,null);
        }
        private static object[] ReadImmutable(GameItem item,object[] previous)
        {
            var result=previous ?? new object[Readers.Length];
            for(int i=0;i<Readers.Length;i++)
            {
                object old=previous?[i];
                object value=Readers[i](item,old);
                if(ReferenceEquals(old,value)) continue;
                if(ReferenceEquals(result,previous)) result=(object[])previous.Clone();
                result[i]=value;
            }
            if(VerifyFrozenTags && audited<512)
            {
                var oracle=new object[Readers.Length];
                Read(item,oracle);
                for(int i=0;i<oracle.Length;i++)
                    if(!Equals(result[i],oracle[i])) throw new InvalidOperationException("Cached item field differs: "+ItemFields[i].Name);
            }
            return result;
        }
        private static void Reflected(GameItem item,object[] row)
        {
            for(int i=0;i<ItemFields.Length;i++) row[i]=ItemFields[i].GetValue(item);
        }
        private static void Read(GameItem item,object[] row)
        {
            if(!VerifyFrozenTags || audited>=512) { Copy(item,row); return; }
            var original=new object[row.Length];
            // Alternating order avoids always favouring the second reader.
            long start=Stopwatch.GetTimestamp();
            if((audited&1)==0) Copy(item,row); else Reflected(item,original);
            long middle=Stopwatch.GetTimestamp();
            if((audited&1)==0) Reflected(item,original); else Copy(item,row);
            long end=Stopwatch.GetTimestamp();
            typedTicks+=(audited&1)==0 ? middle-start : end-middle;
            originalTicks+=(audited&1)==0 ? end-middle : middle-start;
            for(int i=0;i<row.Length;i++)
                if(!object.Equals(row[i],original[i])) throw new InvalidOperationException("Native item field differs: "+ItemFields[i].Name);
            if(++audited==512)
                UnityEngine.Debug.Log(FormattableString.Invariant($"[probably_coop QA] PASS paired-item-fields rows={audited} typed={typed} columns={row.Length} accessor_ms={typedTicks*1000d/Stopwatch.Frequency:F3} reflection_ms={originalTicks*1000d/Stopwatch.Frequency:F3}"));
        }
    }
}
