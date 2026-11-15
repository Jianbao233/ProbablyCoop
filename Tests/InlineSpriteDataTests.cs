using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ProbablyCoop;

internal static class InlineSpriteDataTests
{
    private static int checks;
    internal static void Run()
    {
        checks=0;
        var row=Valid(); var bytes=row.Encode(); var read=InlineSpriteData.Decode(bytes);
        Check(bytes.SequenceEqual(read.Encode()),"deterministic full record roundtrip");
        Check(read.Png.SequenceEqual(row.Png) && read.Vertices.SequenceEqual(row.Vertices) && read.Triangles.SequenceEqual(row.Triangles),"owned data preserved");
        Check(InlineSpriteData.Digest(bytes).Length==64 && InlineSpriteData.Digest(bytes)==InlineSpriteData.Digest(read.Encode()),"digest deterministic");
        for(int n=0;n<bytes.Length;n++) Reject(()=>InlineSpriteData.Decode(bytes.Take(n).ToArray()),"truncated record at "+n);
        Reject(()=>InlineSpriteData.Decode(null),"null record");
        Reject(()=>InlineSpriteData.Decode(new byte[InlineSpriteData.MaxRecordBytes+1]),"oversized record before parsing");
        var tail=bytes.Concat(new byte[]{1}).ToArray(); Reject(()=>InlineSpriteData.Decode(tail),"trailing data");
        Mutate(bytes,0,123,"unknown magic/version");
        var endian=(byte[])bytes.Clone(); Array.Reverse(endian,0,4); Reject(()=>InlineSpriteData.Decode(endian),"wrong endian/magic");
        Mutate(bytes,4,0,"zero texture width"); Mutate(bytes,8,InlineSpriteData.MaxDimension+1,"oversized texture height");
        var flag=(byte[])bytes.Clone(); flag[12]=2; Reject(()=>InlineSpriteData.Decode(flag),"noncanonical boolean");
        Mutate(bytes,13,-1,"filter enum below range"); Mutate(bytes,17,4,"wrap enum above range");
        Mutate(bytes,25,17,"aniso above range"); Mutate(bytes,29,BitConverter.SingleToInt32Bits(float.NaN),"wire NaN mip bias");
        // Counts are fixed-position only until the vertices block begins.
        const int vertexCountOffset=77;
        Mutate(bytes,vertexCountOffset,-1,"negative vertex count");
        Mutate(bytes,vertexCountOffset,2,"less than three vertices");
        Mutate(bytes,vertexCountOffset,InlineSpriteData.MaxVertices+1,"oversized vertex allocation");
        int triangleCountOffset=vertexCountOffset+4+row.Vertices.Length*4;
        Mutate(bytes,triangleCountOffset,-1,"negative index count"); Mutate(bytes,triangleCountOffset,4,"incomplete triangles");
        Mutate(bytes,triangleCountOffset,InlineSpriteData.MaxTriangles+3,"oversized index allocation");
        var index=(byte[])bytes.Clone(); index[triangleCountOffset+4]=255; index[triangleCountOffset+5]=255;
        Reject(()=>InlineSpriteData.Decode(index),"wire out-of-range vertex index");
        int pngLengthOffset=triangleCountOffset+4+row.Triangles.Length*2;
        Mutate(bytes,pngLengthOffset,-1,"negative PNG byte count"); Mutate(bytes,pngLengthOffset,InlineSpriteData.MaxPngBytes+1,"oversized PNG allocation");
        var reordered=(byte[])bytes.Clone(); Array.Copy(bytes,triangleCountOffset, reordered,vertexCountOffset,4); Array.Copy(bytes,vertexCountOffset,reordered,triangleCountOffset,4);
        Reject(()=>InlineSpriteData.Decode(reordered),"reordered count blocks");
        foreach(float value in new[]{float.NaN,float.NegativeInfinity,float.PositiveInfinity})
        {
            var f=Valid(); f.Ppu=value; Reject(()=>f.Encode(),"nonfinite scalar");
            f=Valid(); f.Vertices[0]=value; Reject(()=>f.Encode(),"nonfinite mesh");
        }
        Bad(r=>r.Width=0,"width zero"); Bad(r=>r.Height=-1,"negative height"); Bad(r=>r.Filter=3,"filter upper bound");
        Bad(r=>r.WrapV=-1,"wrap lower bound"); Bad(r=>r.Aniso=-1,"aniso lower bound"); Bad(r=>r.MipBias=101,"mip bias bound");
        Bad(r=>r.X=-1,"rect before texture"); Bad(r=>r.W=0,"zero rect"); Bad(r=>r.W=9,"rect exceeds texture");
        Bad(r=>r.PivotX=r.W+1,"pivot outside rect"); Bad(r=>r.Ppu=0,"zero PPU"); Bad(r=>r.Ppu=100001,"PPU upper bound");
        Bad(r=>r.Left=-1,"negative border"); Bad(r=>{r.Left=3;r.Right=3;},"overlapping border");
        Bad(r=>r.Vertices=null,"null vertices"); Bad(r=>r.Vertices=new float[7],"odd vertices"); Bad(r=>r.Vertices=new float[4],"insufficient vertices");
        Bad(r=>r.Vertices=new float[(InlineSpriteData.MaxVertices+1)*2],"too many vertices"); Bad(r=>r.Vertices[0]=100001,"vertex magnitude");
        Bad(r=>r.Triangles=null,"null triangles"); Bad(r=>r.Triangles=new ushort[4],"nontriangle indices");
        Bad(r=>r.Triangles=new ushort[InlineSpriteData.MaxTriangles+3],"too many indices"); Bad(r=>r.Triangles[0]=4,"index equals vertex count");
        Bad(r=>r.Png=null,"null PNG"); Bad(r=>r.Png=new byte[32],"short PNG"); Bad(r=>r.Png=new byte[InlineSpriteData.MaxPngBytes+1],"oversized PNG");
        foreach(int at in new[]{0,7,8,12,15,16,20,24,25,26,27,28})
        { var r=Valid(); r.Png[at]^=255; Reject(()=>r.Encode(),"invalid PNG header byte "+at); }
        var upper=Valid(); upper.Width=upper.Height=InlineSpriteData.MaxDimension; upper.Png=Png(upper.Width,upper.Height);
        upper.Filter=2; upper.WrapU=upper.WrapV=3; upper.Aniso=16; upper.MipBias=-100;
        upper.Vertices=new float[InlineSpriteData.MaxVertices*2]; upper.Triangles=new ushort[InlineSpriteData.MaxTriangles];
        upper.Triangles[0]=(ushort)(InlineSpriteData.MaxVertices-1);
        Check(InlineSpriteData.Decode(upper.Encode()).Vertices.Length==InlineSpriteData.MaxVertices*2,"maximum valid settings and mesh accepted");
        var reorderedTriangles=Valid(); reorderedTriangles.Triangles=new ushort[]{2,1,0,0,3,2};
        Check(InlineSpriteData.Decode(reorderedTriangles.Encode()).Triangles.SequenceEqual(reorderedTriangles.Triangles),"valid triangle order preserved");
        var independent=InlineSpriteData.Decode(bytes); independent.Png[0]=0; independent.Vertices[0]=99;
        Check(InlineSpriteData.Decode(bytes).Png[0]==137 && InlineSpriteData.Decode(bytes).Vertices[0]!=99,"decode arrays independent of record");
        Console.WriteLine("INLINE_SPRITE_CODEC=PASS checks="+checks);
    }
    private static InlineSpriteData Valid()=>new InlineSpriteData {Width=8,Height=8,Srgb=true,Filter=0,WrapU=1,WrapV=1,Aniso=1,MipBias=0,X=1,Y=2,W=4,H=5,PivotX=1,PivotY=3.75f,Ppu=100,Left=1,Bottom=1,Right=1,Top=1,Vertices=new[]{-.01f,-.0375f,.03f,-.0375f,.03f,.0125f,-.01f,.0125f},Triangles=new ushort[]{0,1,2,2,3,0},Png=Png(8,8)};
    private static void Bad(Action<InlineSpriteData> change,string label) { var r=Valid(); change(r); Reject(()=>r.Encode(),label); }
    private static void Mutate(byte[] original,int offset,int value,string label) { var bytes=(byte[])original.Clone(); Array.Copy(BitConverter.GetBytes(value),0,bytes,offset,4); Reject(()=>InlineSpriteData.Decode(bytes),label); }
    private static void Check(bool condition,string label) { checks++; if(!condition) throw new Exception("Inline sprite test failed: "+label); }
    private static void Reject(Action action,string label)
    {
        checks++; try { action(); } catch(InvalidDataException) { return; } catch(EndOfStreamException) { return; }
        throw new Exception("Inline sprite accepted invalid input: "+label);
    }
    private static byte[] Png(int width,int height)
    {
        using var image=new MemoryStream(); image.Write(new byte[]{137,80,78,71,13,10,26,10});
        using var ihdr=new MemoryStream(); BE(ihdr,(uint)width); BE(ihdr,(uint)height); ihdr.Write(new byte[]{8,6,0,0,0}); Chunk(image,"IHDR",ihdr.ToArray());
        using var packed=new MemoryStream(); using(var z=new ZLibStream(packed,CompressionLevel.Fastest,true))
        { var scan=new byte[width*4+1]; for(int x=0;x<width;x++) {scan[x*4+1]=31;scan[x*4+2]=127;scan[x*4+3]=223;scan[x*4+4]=255;} for(int y=0;y<height;y++) z.Write(scan); }
        Chunk(image,"IDAT",packed.ToArray()); Chunk(image,"IEND",Array.Empty<byte>()); return image.ToArray();
    }
    private static void Chunk(Stream output,string kind,byte[] value)
    {
        byte[] name=System.Text.Encoding.ASCII.GetBytes(kind); BE(output,(uint)value.Length); output.Write(name); output.Write(value); uint crc=0xffffffff;
        foreach(byte b in name.Concat(value)) {crc^=b;for(int k=0;k<8;k++) crc=(crc&1)!=0 ? (crc>>1)^0xedb88320 : crc>>1;} BE(output,~crc);
    }
    private static void BE(Stream s,uint value) {s.WriteByte((byte)(value>>24));s.WriteByte((byte)(value>>16));s.WriteByte((byte)(value>>8));s.WriteByte((byte)value);}
}
