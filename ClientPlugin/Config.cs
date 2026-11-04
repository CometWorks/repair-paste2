using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using ClientPlugin.Settings.Elements;
using ClientPlugin.Tools;

namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    #region Options

    private bool enabled = true;

    #endregion

    #region User interface

    [XmlIgnore]
    public readonly string Title = "Repair Paste";

    [Checkbox(
        description: "Paste a blueprint over the ship it was made from: the preview snaps onto the matching blocks, only the missing blocks are placed"
    )]
    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
