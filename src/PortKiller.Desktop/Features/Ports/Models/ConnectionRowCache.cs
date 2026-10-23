using System.Collections.Generic;
using System.Linq;
using PortKiller.Models;

namespace PortKiller.Features.Ports.Models;

/// <summary>仅保留当前网络快照中暂被过滤的显示行；连接消失即回收，重复身份逐条保留。</summary>
internal sealed class ConnectionRowCache
{
    private readonly Dictionary<PortConnection.ConnectionIdentity, List<PortConnectionItem>> _hidden = new();
    private Dictionary<PortConnection.ConnectionIdentity, int> _live = new();
    internal void UpdateSnapshot(IReadOnlyList<PortConnection> items)
    {
        _live = new();
        foreach (var item in items) _live[item.Identity] = _live.GetValueOrDefault(item.Identity) + 1;
        foreach (var key in _hidden.Keys.Where(key => !_live.ContainsKey(key)).ToArray()) _hidden.Remove(key);
        // 相同键的重复记录数量也必须收敛，不能因重复数减少而保留历史行。
        if (_hidden.Count > 0)
        {
            foreach (var pair in _hidden)
            {
                int maximum = _live[pair.Key];
                if (pair.Value.Count > maximum) pair.Value.RemoveRange(maximum, pair.Value.Count - maximum);
            }
        }
    }
    internal PortConnectionItem? Take(PortConnection snapshot)
    {
        if (!_hidden.TryGetValue(snapshot.Identity, out var list) || list.Count == 0) return null;
        var row = list[^1]; list.RemoveAt(list.Count - 1);
        if (list.Count == 0) _hidden.Remove(snapshot.Identity);
        row.ApplySnapshot(snapshot);
        return row;
    }
    internal void Store(PortConnectionItem row)
    {
        if (!_live.TryGetValue(row.Identity, out int maximum)) return;
        row.IsChecked = false; row.IsFocused = false;
        if (!_hidden.TryGetValue(row.Identity, out var list)) _hidden[row.Identity] = list = new();
        if (list.Count < maximum) list.Add(row);
    }
    internal void Clear() { _hidden.Clear(); _live.Clear(); }
}
