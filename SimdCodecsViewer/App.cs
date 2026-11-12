using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace SimdCodecsViewer;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        AddWorkbenchStyles();
    }
    /// <summary>
    /// 全局视觉（纯 C# 样式，无 XAML 依赖，NativeAOT 友好）：
    /// 控件统一深色底 + 圆角，primary 按钮用主色填充形成层级。
    /// </summary>
    private void AddWorkbenchStyles()
    {
        // ---- 按钮：默认为次级样式（描边 + 面板底） ----
        var btn = new Style(x => x.OfType<Button>());
        btn.Setters.Add(new Setter(Button.BackgroundProperty, Ui.B(Ui.PanelAlt)));
        btn.Setters.Add(new Setter(Button.ForegroundProperty, Ui.B(Ui.Text)));
        btn.Setters.Add(new Setter(Button.BorderBrushProperty, Ui.B(Ui.Border)));
        btn.Setters.Add(new Setter(Button.CornerRadiusProperty, new CornerRadius(6)));
        btn.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 6)));
        btn.Setters.Add(new Setter(Button.MinHeightProperty, 32d));
        Styles.Add(btn);

        var btnOver = new Style(x => x.OfType<Button>().Class(":pointerover").Not(
            x => x.Class("primary")));
        btnOver.Setters.Add(new Setter(Button.BackgroundProperty, Ui.B(Ui.Hover)));
        btnOver.Setters.Add(new Setter(Button.BorderBrushProperty, Ui.B(Ui.Accent)));
        Styles.Add(btnOver);

        var btnPressed = new Style(x => x.OfType<Button>().Class(":pressed").Not(
            x => x.Class("primary")));
        btnPressed.Setters.Add(new Setter(Button.BackgroundProperty, Ui.B(Ui.Panel)));
        Styles.Add(btnPressed);

        // ---- 主按钮：主色填充 ----
        var primary = new Style(x => x.OfType<Button>().Class("primary"));
        primary.Setters.Add(new Setter(Button.BackgroundProperty, Ui.B(Ui.Accent)));
        primary.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
        primary.Setters.Add(new Setter(Button.BorderBrushProperty, Ui.B(Ui.Accent)));
        Styles.Add(primary);

        var primaryOver = new Style(x => x.OfType<Button>().Class("primary").Class(":pointerover"));
        primaryOver.Setters.Add(new Setter(Button.BackgroundProperty, Ui.B(Ui.AccentDim)));
        primaryOver.Setters.Add(new Setter(Button.BorderBrushProperty, Ui.B(Ui.AccentDim)));
        Styles.Add(primaryOver);

        // ---- 输入框 ----
        var text = new Style(x => x.OfType<TextBox>());
        text.Setters.Add(new Setter(TextBox.BackgroundProperty, Ui.B(Ui.Preview)));
        text.Setters.Add(new Setter(TextBox.ForegroundProperty, Ui.B(Ui.Text)));
        text.Setters.Add(new Setter(TextBox.BorderBrushProperty, Ui.B(Ui.Border)));
        text.Setters.Add(new Setter(TextBox.CornerRadiusProperty, new CornerRadius(6)));
        Styles.Add(text);

        // ---- 复选框文字颜色跟随面板 ----
        var check = new Style(x => x.OfType<CheckBox>());
        check.Setters.Add(new Setter(CheckBox.ForegroundProperty, Ui.B(Ui.Text)));
        Styles.Add(check);

        // ---- 列表行 ----
        var list = new Style(x => x.OfType<ListBox>());
        list.Setters.Add(new Setter(ListBox.BackgroundProperty, Ui.B(Ui.Panel)));
        Styles.Add(list);

        var item = new Style(x => x.OfType<ListBoxItem>());
        item.Setters.Add(new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(6)));
        item.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(2, 0)));
        Styles.Add(item);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
