using Godot;

namespace EsoTest;

/// <summary>Shared, compact visual language for the source browser and world tools.</summary>
public static class EsoUiStyles
{
    public static readonly Color Background = new("#101821");
    public static readonly Color Surface = new("#172431");
    public static readonly Color Raised = new("#223443");
    public static readonly Color Accent = new("#79c9d5");
    public static readonly Color Muted = new("#a0b6c5");
    public static readonly Color Text = new("#e7f1f5");

    public static StyleBoxFlat Box(Color fill, int radius=10, int border=0,
        Color? borderColor=null,int pad=0)
    {
        return new StyleBoxFlat
        {
            BgColor=fill,
            CornerRadiusTopLeft=radius,CornerRadiusTopRight=radius,
            CornerRadiusBottomLeft=radius,CornerRadiusBottomRight=radius,
            BorderWidthLeft=border,BorderWidthRight=border,
            BorderWidthTop=border,BorderWidthBottom=border,
            BorderColor=borderColor??new Color("#3b5364"),
            ContentMarginLeft=pad,ContentMarginRight=pad,
            ContentMarginTop=pad,ContentMarginBottom=pad
        };
    }

    public static void ButtonStyle(Button button,bool active=false)
    {
        button.AddThemeStyleboxOverride("normal",Box(active?new Color("#245361"):Surface,7));
        button.AddThemeStyleboxOverride("hover",Box(new Color("#315667"),7));
        button.AddThemeStyleboxOverride("pressed",Box(new Color("#1f5666"),7));
        button.AddThemeStyleboxOverride("focus",Box(new Color(0,0,0,0),7,1,Accent));
        button.AddThemeColorOverride("font_color",active?Text:Muted);
        button.AddThemeColorOverride("font_hover_color",Text);
        button.AddThemeColorOverride("font_pressed_color",Text);
        button.AddThemeConstantOverride("h_separation",9);
        button.CustomMinimumSize=new Vector2(button.CustomMinimumSize.X,38);
    }

    public static void Scrollbar(ScrollBar bar)
    {
        bar.CustomMinimumSize=new Vector2(13,13);
        bar.AddThemeStyleboxOverride("scroll",Box(new Color("#263645"),6));
        bar.AddThemeStyleboxOverride("grabber",Box(new Color("#688a9d"),6));
        bar.AddThemeStyleboxOverride("grabber_highlight",Box(Accent,6));
        bar.AddThemeStyleboxOverride("grabber_pressed",Box(new Color("#91e6ed"),6));
    }

    public static Label Heading(string text,int size=17)
    {
        var label=new Label {Text=text};
        label.AddThemeFontSizeOverride("font_size",size);
        label.AddThemeColorOverride("font_color",Text);
        return label;
    }

    public static Label Note(string text)
    {
        var label=new Label {Text=text,AutowrapMode=TextServer.AutowrapMode.WordSmart};
        label.AddThemeColorOverride("font_color",Muted);
        label.AddThemeFontSizeOverride("font_size",13);
        return label;
    }
}
