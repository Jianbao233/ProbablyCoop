namespace ProbablyCoop.Core;

// Native rich text uses one replacement character per UTF-16 code unit.
// Control characters keep their original value and take the native path.
internal sealed class TooltipFill
{
    private const int MaxLength=256;
    private readonly string[] spaces=new string[MaxLength+1];
    private readonly string[] blocks=new string[MaxLength+1];

    internal bool TryGet(string text,char replace,out string value)
    {
        value=null;
        if(text==null || text.Length>MaxLength || (replace!=' ' && replace!='█')) return false;
        foreach(char c in text) if(char.IsControl(c)) return false;
        var rows=replace==' ' ? spaces : blocks;
        value=rows[text.Length] ??= new string(replace,text.Length);
        return true;
    }
}
