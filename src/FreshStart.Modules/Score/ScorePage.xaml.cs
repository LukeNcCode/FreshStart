using System.Windows;

namespace FreshStart.Modules.Score;

public partial class ScorePage
{
    public ScorePage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ScoreViewModel)?.LoadCommand.Execute(null);
    }
}
