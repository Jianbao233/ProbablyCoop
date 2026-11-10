using System;
using System.Reflection;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// Immutable native tag state, without boxed primitives or native references.
// The adapter uses this path only when the installed reflection contract matches
// every name/type/order; an extended game contract keeps the generic capture.
internal readonly struct NativeTagRow
{
    internal static readonly string[] Names={ "valueEnabled","internalValueString","valueInt","valueFloat",
        "valueLong","valueDouble","valueBool","<identifier>k__BackingField","<identifierName>k__BackingField" };
    private static readonly Type[] Types={typeof(bool),typeof(string),typeof(int),typeof(float),typeof(long),typeof(double),typeof(bool),typeof(string),typeof(string)};
    internal readonly bool Enabled,Boolean;
    internal readonly string Text,Identifier,DisplayName;
    internal readonly int Integer;
    internal readonly float Single;
    internal readonly long Long;
    internal readonly double Double;
    internal NativeTagRow(bool enabled,string text,int integer,float single,long longValue,double doubleValue,bool boolean,string identifier,string displayName)
    { Enabled=enabled; Text=text; Integer=integer; Single=single; Long=longValue; Double=doubleValue; Boolean=boolean; Identifier=identifier; DisplayName=displayName; }
    internal bool SameValues(in NativeTagRow other) =>
        Enabled==other.Enabled && Boolean==other.Boolean && Integer==other.Integer && Long==other.Long &&
        BitConverter.SingleToInt32Bits(Single)==BitConverter.SingleToInt32Bits(other.Single) &&
        BitConverter.DoubleToInt64Bits(Double)==BitConverter.DoubleToInt64Bits(other.Double) &&
        Text==other.Text && Identifier==other.Identifier && DisplayName==other.DisplayName;
    internal static JObject Resolve(JProperty property,JObject basis)
    {
        if(property.Value is JObject explicitRow) return explicitRow;
        if(property.Value.Type==JTokenType.Boolean && !(bool)property.Value && basis?[property.Name] is JObject baseRow)
            return baseRow;
        throw new InvalidDataException("Invalid native tag row");
    }
    internal sealed class Layout
    {
        internal readonly string[] Names;
        internal readonly int[] Columns;
        internal Layout(string[] names,int[] columns) {Names=names; Columns=columns;}
    }
    internal static readonly Layout Canonical=new Layout(Names,new[]{0,1,2,3,4,5,6,7,8});
    internal static Layout GetLayout(FieldInfo[] fields)
    {
        if(fields==null || fields.Length!=Names.Length) return null;
        var names=new string[fields.Length]; var columns=new int[fields.Length]; var seen=new bool[fields.Length];
        for(int i=0;i<fields.Length;i++)
        {
            int column=Array.IndexOf(Names,fields[i].Name);
            if(fields[i].IsStatic || column<0 || seen[column] || fields[i].FieldType!=Types[column]) return null;
            names[i]=fields[i].Name; columns[i]=column; seen[column]=true;
        }
        return new Layout(names,columns);
    }
    // Materialization is kept for differential tests, not the production encoder.
    internal object Value(int column)=>column switch {
        0=>Enabled,1=>Text,2=>Integer,3=>Single,4=>Long,5=>Double,6=>Boolean,7=>Identifier,8=>DisplayName,
        _=>throw new ArgumentOutOfRangeException(nameof(column)) };
}
