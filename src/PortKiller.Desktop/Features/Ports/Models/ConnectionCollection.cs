using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PortKiller.Models;

/// <summary>保留 ItemsSource；大批变化用范围通知，让虚拟化面板继续复用容器池。</summary>
public sealed class ConnectionCollection : ObservableCollection<PortConnectionItem>
{
    internal void ApplySnapshot(IReadOnlyList<PortConnectionItem> target)
    {
        CheckReentrancy();
        int common = System.Math.Min(Count, target.Count);
        for (int i = 0; i < common;)
        {
            if (ReferenceEquals(this[i], target[i])) { i++; continue; }
            int start = i;
            var previous = new List<PortConnectionItem>();
            var next = new List<PortConnectionItem>();
            do
            {
                previous.Add(this[i]);
                next.Add(target[i]);
                Items[i] = target[i];
                i++;
            } while (i < common && !ReferenceEquals(this[i], target[i]));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace, next, previous, start));
        }
        if (Count > target.Count)
        {
            int start = target.Count;
            var removed = new List<PortConnectionItem>(Count - start);
            for (int i = start; i < Count; i++) removed.Add(this[i]);
            while (Count > start) Items.RemoveAt(Count - 1);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove, removed, start));
        }
        else if (Count < target.Count)
        {
            int start = Count;
            var added = new List<PortConnectionItem>(target.Count - start);
            for (int i = start; i < target.Count; i++) { added.Add(target[i]); Items.Add(target[i]); }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, added, start));
        }
    }
}
