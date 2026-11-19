using System.Windows.Controls;

namespace WristMusic.Views;
public partial class DarkPlayerPage : UserControl
{
    public DarkPlayerPage() { InitializeComponent(); SeekSliderInput.Attach(Progress); }
}
