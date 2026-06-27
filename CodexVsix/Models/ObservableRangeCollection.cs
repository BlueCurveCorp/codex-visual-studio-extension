using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace CodexVsix.Models;

public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void ReplaceAll(IEnumerable<T> items)
    {
        this.CheckReentrancy();

        this._suppressNotifications = true;
        try
        {
            this.Items.Clear();
            foreach (T? item in items)
            {
                this.Items.Add(item);
            }
        }
        finally
        {
            this._suppressNotifications = false;
        }

        this.OnPropertyChanged(new PropertyChangedEventArgs(nameof(this.Count)));
        this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!this._suppressNotifications)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!this._suppressNotifications)
        {
            base.OnPropertyChanged(e);
        }
    }
}
