using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal static class NativeSpriteTransfer
{
    private sealed class Captured
    {
        internal Texture2D Texture;
        internal uint Revision;
        internal InlineSpriteData Data;
        internal string Payload,Hash;
    }
    private sealed class Received
    {
        internal Sprite Sprite;
        internal Texture2D Texture;
        internal string Payload;
    }
    private static ConditionalWeakTable<Sprite,Captured> captured=new ConditionalWeakTable<Sprite,Captured>();
    private static readonly Dictionary<string,Received> received=new Dictionary<string,Received>(StringComparer.Ordinal);
    private const long MaxResourceBytes=64L*1024*1024;
    private static long resourceBytes;
    internal static bool HasCapture(Sprite sprite) => captured.TryGetValue(sprite,out _);

    internal static JObject Capture(Sprite sprite)
    {
        var texture=sprite.texture;
        if(sprite.packed || texture==null || !texture.isReadable || texture.mipmapCount!=1 ||
            (texture.format!=TextureFormat.RGBA32 && texture.format!=TextureFormat.RGB24 && texture.format!=TextureFormat.ARGB32) ||
            texture.width>InlineSpriteData.MaxDimension || texture.height>InlineSpriteData.MaxDimension)
            throw Unsupported(sprite,texture);
        var rect=sprite.rect; var pivot=sprite.pivot; var border=sprite.border;
        var vertices=sprite.vertices; var triangles=sprite.triangles;
        var data=new InlineSpriteData {
            Width=texture.width,Height=texture.height,Srgb=texture.isDataSRGB,
            Filter=(int)texture.filterMode,WrapU=(int)texture.wrapModeU,WrapV=(int)texture.wrapModeV,
            Aniso=texture.anisoLevel,MipBias=texture.mipMapBias,
            X=rect.x,Y=rect.y,W=rect.width,H=rect.height,PivotX=pivot.x,PivotY=pivot.y,Ppu=sprite.pixelsPerUnit,
            Left=border.x,Bottom=border.y,Right=border.z,Top=border.w,
            Vertices=new float[vertices.Length*2],Triangles=triangles
        };
        for(int i=0;i<vertices.Length;i++) { data.Vertices[i*2]=vertices[i].x; data.Vertices[i*2+1]=vertices[i].y; }
        if(captured.TryGetValue(sprite,out var old) && old.Texture==texture && old.Revision==texture.updateCount && Same(old.Data,data))
            return Descriptor(old);
        data.Png=texture.EncodeToPNG();
        byte[] bytes=data.Encode();
        var entry=new Captured { Texture=texture,Revision=texture.updateCount,Data=data,
            Payload=Convert.ToBase64String(bytes),Hash=InlineSpriteData.Digest(bytes) };
        captured.Remove(sprite); captured.Add(sprite,entry);
        return Descriptor(entry);
    }
    private static JObject Descriptor(Captured entry) => new JObject { ["inline"]=entry.Payload,["sha256"]=entry.Hash };
    private static bool Same(InlineSpriteData a,InlineSpriteData b) =>
        a.Width==b.Width && a.Height==b.Height && a.Srgb==b.Srgb && a.Filter==b.Filter && a.WrapU==b.WrapU && a.WrapV==b.WrapV &&
        a.Aniso==b.Aniso && a.MipBias==b.MipBias && a.X==b.X && a.Y==b.Y && a.W==b.W && a.H==b.H &&
        a.PivotX==b.PivotX && a.PivotY==b.PivotY && a.Ppu==b.Ppu && a.Left==b.Left && a.Bottom==b.Bottom &&
        a.Right==b.Right && a.Top==b.Top && a.Vertices.SequenceEqual(b.Vertices) && a.Triangles.SequenceEqual(b.Triangles);
    private static Exception Unsupported(Sprite sprite,Texture2D texture) => new NotSupportedException(
        "Sprite has no atlas identity and cannot be transferred: sprite=["+sprite.name+"] texture=["+(texture?.name??"null")+
        "] readable="+(texture!=null && texture.isReadable)+" packed="+sprite.packed+
        " size="+(texture==null ? "null" : texture.width+"x"+texture.height)+" format="+(texture==null ? "null" : texture.format.ToString())+
        ". Supported fallback: readable, unpacked RGB24/RGBA32/ARGB32 texture, no mipmaps, at most 1024x1024.");

    internal static Sprite Read(JObject descriptor)
    {
        if(descriptor.Count!=2 || descriptor["inline"]?.Type!=JTokenType.String || descriptor["sha256"]?.Type!=JTokenType.String)
            throw new InvalidDataException("Invalid inline sprite descriptor");
        string payload=(string)descriptor["inline"],hash=(string)descriptor["sha256"];
        if(hash.Length!=64 || payload.Length>(InlineSpriteData.MaxRecordBytes+2)/3*4)
            throw new InvalidDataException("Inline sprite descriptor exceeds bounds");
        if(received.TryGetValue(hash,out var cached))
        {
            if(!String.Equals(payload,cached.Payload,StringComparison.Ordinal)) throw new InvalidDataException("Inline sprite digest collision");
            return cached.Sprite;
        }
        byte[] bytes=Convert.FromBase64String(payload);
        if(!String.Equals(hash,InlineSpriteData.Digest(bytes),StringComparison.Ordinal)) throw new InvalidDataException("Inline sprite digest mismatch");
        var data=InlineSpriteData.Decode(bytes);
        // Readable textures keep CPU and GPU storage. Also charge retained
        // UTF-16 payloads and mesh data, so small images with large PNG metadata
        // cannot evade the session budget.
        long allocation=(long)data.Width*data.Height*8+(long)payload.Length*2+data.Vertices.Length*8L+data.Triangles.Length*4L;
        if(received.Count>=1024 || allocation>MaxResourceBytes-resourceBytes) throw new InvalidDataException("Inline sprite session resource limit exceeded");
        Texture2D texture=null; Sprite sprite=null;
        try
        {
            texture=new Texture2D(data.Width,data.Height,TextureFormat.RGBA32,false,!data.Srgb);
            if(!texture.LoadImage(data.Png,false) || texture.width!=data.Width || texture.height!=data.Height || texture.isDataSRGB!=data.Srgb)
                throw new InvalidDataException("Inline sprite PNG decode failed");
            texture.name="probably_coop_inline_"+hash;
            texture.filterMode=(FilterMode)data.Filter; texture.wrapModeU=(TextureWrapMode)data.WrapU; texture.wrapModeV=(TextureWrapMode)data.WrapV;
            texture.anisoLevel=data.Aniso; texture.mipMapBias=data.MipBias;
            sprite=Sprite.Create(texture,new Rect(data.X,data.Y,data.W,data.H),new Vector2(data.PivotX/data.W,data.PivotY/data.H),
                data.Ppu,0,SpriteMeshType.Tight,new Vector4(data.Left,data.Bottom,data.Right,data.Top));
            if(sprite==null) throw new InvalidDataException("Inline sprite construction failed");
            sprite.name=texture.name;
            var mesh=new Vector2[data.Vertices.Length/2];
            // Unity exposes vertices in local units, but OverrideGeometry takes
            // pixel positions relative to the sprite rectangle. FullRect sprites
            // silently reject this call, so construct Tight and verify readback.
            for(int i=0;i<mesh.Length;i++)
                mesh[i]=new Vector2(MeshPixel(data.Vertices[i*2],data.Ppu,data.PivotX,data.W),
                    MeshPixel(data.Vertices[i*2+1],data.Ppu,data.PivotY,data.H));
            sprite.OverrideGeometry(mesh,data.Triangles);
            var actual=sprite.vertices;
            if(actual.Length!=mesh.Length || !sprite.triangles.SequenceEqual(data.Triangles))
                throw new InvalidDataException("Inline sprite geometry was not restored");
            for(int i=0;i<actual.Length;i++)
                if(Math.Abs(((double)actual[i].x-data.Vertices[i*2])*data.Ppu)>0.001 ||
                    Math.Abs(((double)actual[i].y-data.Vertices[i*2+1])*data.Ppu)>0.001)
                    throw new InvalidDataException("Inline sprite vertex was not restored");
            received.Add(hash,new Received { Sprite=sprite,Texture=texture,Payload=payload }); resourceBytes+=allocation;
            return sprite;
        }
        catch
        {
            if(sprite!=null) UnityEngine.Object.Destroy(sprite);
            if(texture!=null) UnityEngine.Object.Destroy(texture);
            throw;
        }
    }
    private static float MeshPixel(float local,float ppu,float pivot,float extent)
    {
        double pixel=(double)local*ppu+pivot;
        if(pixel < -0.001 || pixel > extent+0.001)
            throw new InvalidDataException("Inline sprite vertex is outside its rectangle");
        return (float)Math.Max(0,Math.Min(extent,pixel));
    }
    internal static void Clear()
    {
        foreach(var entry in received.Values)
        { if(entry.Sprite!=null) UnityEngine.Object.Destroy(entry.Sprite); if(entry.Texture!=null) UnityEngine.Object.Destroy(entry.Texture); }
        received.Clear(); resourceBytes=0; captured=new ConditionalWeakTable<Sprite,Captured>();
    }
}
