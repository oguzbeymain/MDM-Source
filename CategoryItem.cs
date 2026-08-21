using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace DownloadMuck
{
    public class CategoryItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private string _id = "";
        public string Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        private string _icon = "📁";
        public string Icon
        {
            get => _icon;
            set => SetField(ref _icon, value);
        }

        private bool _isBuiltin;
        public bool IsBuiltin
        {
            get => _isBuiltin;
            set => SetField(ref _isBuiltin, value);
        }

        private string? _parentId;
        public string? ParentId
        {
            get => _parentId;
            set => SetField(ref _parentId, value);
        }

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetField(ref _isExpanded, value);
        }

        private int _depth;
        public int Depth
        {
            get => _depth;
            set
            {
                if (!SetField(ref _depth, value)) return;
                OnPropertyChanged(nameof(IndentMargin));
                OnPropertyChanged(nameof(NestBackground));
                OnPropertyChanged(nameof(HasChildren));
            }
        }

        public ObservableCollection<CategoryItem> Children { get; } = new();

        public HashSet<string>? Extensions { get; set; }

        public string DisplayLabel => $"{Icon}  {Name}";

        public bool HasChildren => Children.Count > 0;

        public Thickness IndentMargin => new(8 + Depth * 14, 0, 0, 0);

        /// <summary>Sadece hiyerarşi için cok hafif sol cizgi; secim turuncusuyle karismasin.</summary>
        public System.Windows.Media.Brush NestBackground => System.Windows.Media.Brushes.Transparent;

        public Thickness NestAccentThickness => Depth > 0 ? new Thickness(2, 0, 0, 0) : new Thickness(0);
        public System.Windows.Media.Brush NestAccentBrush =>
            Depth > 0
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0x88, 0x88, 0x88))
                : System.Windows.Media.Brushes.Transparent;

        public void NotifyChildrenChanged()
        {
            OnPropertyChanged(nameof(HasChildren));
            OnPropertyChanged(nameof(NestBackground));
            OnPropertyChanged(nameof(NestAccentThickness));
            OnPropertyChanged(nameof(NestAccentBrush));
        }

        public IEnumerable<CategoryItem> Flatten()
        {
            yield return this;
            foreach (var child in Children)
            foreach (var x in child.Flatten())
                yield return x;
        }

        public bool IsDescendantOf(CategoryItem possibleAncestor)
        {
            if (possibleAncestor == null || ReferenceEquals(possibleAncestor, this)) return false;
            foreach (var c in possibleAncestor.Flatten())
            {
                if (ReferenceEquals(c, possibleAncestor)) continue; // Flatten self'i de verir
                if (ReferenceEquals(c, this)) return true;
            }
            return false;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            if (name is nameof(Icon) or nameof(Name))
                OnPropertyChanged(nameof(DisplayLabel));
            return true;
        }
    }
}
