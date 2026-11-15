using System;
using System.IO;
using System.Security.Cryptography;

namespace ProbablyCoop;

// A versioned data record, never a runtime object or a native reference.
// Kept independent of Unity so malformed and oversized records can be tested.
internal sealed class InlineSpriteData
{
    internal const int MaxDimension=1024, MaxPngBytes=512*1024, MaxVertices=4096, MaxTriangles=12288;
    internal const int MaxRecordBytes=MaxPngBytes+MaxVertices*8+MaxTriangles*2+256;
    internal int Width,Height,Filter,WrapU,WrapV,Aniso;
    internal bool Srgb;
    internal float MipBias,X,Y,W,H,PivotX,PivotY,Ppu,Left,Bottom,Right,Top;
    internal float[] Vertices;
    internal ushort[] Triangles;
    internal byte[] Png;

    internal byte[] Encode()
    {
        Validate();
        using var stream=new MemoryStream();
        using var writer=new BinaryWriter(stream);
        writer.Write(0x31535043); // CPS1
        writer.Write(Width); writer.Write(Height); writer.Write(Srgb);
        writer.Write(Filter); writer.Write(WrapU); writer.Write(WrapV); writer.Write(Aniso); writer.Write(MipBias);
        foreach(var f in new[]{X,Y,W,H,PivotX,PivotY,Ppu,Left,Bottom,Right,Top}) writer.Write(f);
        writer.Write(Vertices.Length/2); foreach(float f in Vertices) writer.Write(f);
        writer.Write(Triangles.Length); foreach(ushort v in Triangles) writer.Write(v);
        writer.Write(Png.Length); writer.Write(Png);
        return stream.ToArray();
    }

    internal static InlineSpriteData Decode(byte[] bytes)
    {
        if(bytes==null || bytes.Length>MaxRecordBytes) throw new InvalidDataException("Inline sprite record exceeds bounds");
        using var stream=new MemoryStream(bytes,false);
        using var reader=new BinaryReader(stream);
        if(reader.ReadInt32()!=0x31535043) throw new InvalidDataException("Unknown inline sprite format");
        var row=new InlineSpriteData { Width=reader.ReadInt32(),Height=reader.ReadInt32() };
        byte srgb=reader.ReadByte(); if(srgb>1) throw new InvalidDataException("Invalid inline sprite color space"); row.Srgb=srgb==1;
        row.Filter=reader.ReadInt32(); row.WrapU=reader.ReadInt32(); row.WrapV=reader.ReadInt32(); row.Aniso=reader.ReadInt32(); row.MipBias=reader.ReadSingle();
        row.X=reader.ReadSingle(); row.Y=reader.ReadSingle(); row.W=reader.ReadSingle(); row.H=reader.ReadSingle();
        row.PivotX=reader.ReadSingle(); row.PivotY=reader.ReadSingle(); row.Ppu=reader.ReadSingle();
        row.Left=reader.ReadSingle(); row.Bottom=reader.ReadSingle(); row.Right=reader.ReadSingle(); row.Top=reader.ReadSingle();
        int vertices=reader.ReadInt32(); if(vertices<3 || vertices>MaxVertices) throw new InvalidDataException("Invalid inline sprite vertex count");
        row.Vertices=new float[vertices*2]; for(int i=0;i<row.Vertices.Length;i++) row.Vertices[i]=reader.ReadSingle();
        int triangles=reader.ReadInt32(); if(triangles<3 || triangles>MaxTriangles || triangles%3!=0) throw new InvalidDataException("Invalid inline sprite triangle count");
        row.Triangles=new ushort[triangles]; for(int i=0;i<triangles;i++) row.Triangles[i]=reader.ReadUInt16();
        int length=reader.ReadInt32(); if(length<33 || length>MaxPngBytes || length!=stream.Length-stream.Position) throw new InvalidDataException("Invalid inline sprite PNG length");
        row.Png=reader.ReadBytes(length);
        row.Validate();
        return row;
    }

    internal void Validate()
    {
        if(Width<1 || Height<1 || Width>MaxDimension || Height>MaxDimension || Filter<0 || Filter>2 ||
            WrapU<0 || WrapU>3 || WrapV<0 || WrapV>3 || Aniso<0 || Aniso>16)
            throw new InvalidDataException("Invalid inline sprite texture settings");
        foreach(var f in new[]{MipBias,X,Y,W,H,PivotX,PivotY,Ppu,Left,Bottom,Right,Top})
            if(float.IsNaN(f) || float.IsInfinity(f)) throw new InvalidDataException("Non-finite inline sprite value");
        if(X<0 || Y<0 || W<=0 || H<=0 || X+W>Width || Y+H>Height || Ppu<=0 || Ppu>100000 ||
            PivotX<0 || PivotY<0 || PivotX>W || PivotY>H || Left<0 || Bottom<0 || Right<0 || Top<0 ||
            Left+Right>W || Bottom+Top>H || Math.Abs(MipBias)>100)
            throw new InvalidDataException("Invalid inline sprite geometry");
        if(Vertices==null || Vertices.Length<6 || Vertices.Length>MaxVertices*2 || Vertices.Length%2!=0 ||
            Triangles==null || Triangles.Length<3 || Triangles.Length>MaxTriangles || Triangles.Length%3!=0)
            throw new InvalidDataException("Invalid inline sprite mesh");
        foreach(float value in Vertices)
            if(float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value)>100000)
                throw new InvalidDataException("Invalid inline sprite vertex");
        foreach(ushort index in Triangles) if(index>=Vertices.Length/2) throw new InvalidDataException("Invalid inline sprite triangle");
        ValidatePng(Png,Width,Height);
    }

    private static void ValidatePng(byte[] bytes,int width,int height)
    {
        byte[] signature={137,80,78,71,13,10,26,10};
        if(bytes==null || bytes.Length<33 || bytes.Length>MaxPngBytes) throw new InvalidDataException("Invalid inline sprite PNG size");
        for(int i=0;i<signature.Length;i++) if(bytes[i]!=signature[i]) throw new InvalidDataException("Invalid inline sprite PNG signature");
        if(BigEndian(bytes,8)!=13 || bytes[12]!=73 || bytes[13]!=72 || bytes[14]!=68 || bytes[15]!=82 ||
            BigEndian(bytes,16)!=width || BigEndian(bytes,20)!=height || bytes[24]!=8 ||
            (bytes[25]!=2 && bytes[25]!=6) || bytes[26]!=0 || bytes[27]!=0 || bytes[28]!=0)
            throw new InvalidDataException("Invalid inline sprite PNG header");
    }
    private static uint BigEndian(byte[] b,int i) => ((uint)b[i]<<24)|((uint)b[i+1]<<16)|((uint)b[i+2]<<8)|b[i+3];
    internal static string Digest(byte[] bytes)
    {
        using var sha=SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
    }
}
