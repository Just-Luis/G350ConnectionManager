using System.Windows;
using System.Windows.Controls;

namespace G350ConnectionManager.Views;

public partial class PageHeader : UserControl
{
    public PageHeader()
    {
        InitializeComponent();
        // XAML consumers set Title/Subtitle while parsing *this* element, which
        // happens before the child TextBlocks exist. Re-apply once we're loaded.
        Loaded += (_, _) => Sync();
        Sync();
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>
    /// XAML sets Title/Subtitle before InitializeComponent has run, so the
    /// callback fires while TitleText/SubtitleText are still null. Guard and
    /// re-sync from the constructor instead of dereferencing blindly.
    /// </summary>
    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PageHeader)d).Sync();

    private void Sync()
    {
        if (TitleText is null || SubtitleText is null) return;
        TitleText.Text = Title;
        SubtitleText.Text = Subtitle;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TitleProperty || e.Property == SubtitleProperty)
            Sync();
    }
}
