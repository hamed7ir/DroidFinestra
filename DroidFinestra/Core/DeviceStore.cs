using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace DroidFinestra.Core
{
    /// <summary>
    /// Persists the list of <see cref="DeviceProfile"/>s to profiles.json in the data folder (via
    /// <see cref="StoragePaths"/> — Documents-first, RT-safe). This is Finestra's ConnectionStore with the
    /// element type swapped: same Newtonsoft default settings, same atomic save (temp + File.Replace), same
    /// guarded load (a read failure yields an empty store rather than throwing), same Find/AddOrUpdate/Remove.
    /// </summary>
    public sealed class DeviceStore
    {
        public List<DeviceProfile> Items { get; private set; } = new List<DeviceProfile>();

        private static DeviceStore _instance;
        public static DeviceStore Instance => _instance ?? (_instance = Load());

        private static DeviceStore Load()
        {
            var store = new DeviceStore();
            try
            {
                string path = StoragePaths.ProfilesFile;
                if (File.Exists(path))
                {
                    var items = JsonConvert.DeserializeObject<List<DeviceProfile>>(File.ReadAllText(path));
                    if (items != null)
                    {
                        items.RemoveAll(p => p == null);
                        foreach (var p in items) p.Normalize();
                        store.Items = items;
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[PROFILES] load failed: " + ex.Message); }
            return store;
        }

        public void Save()
        {
            try
            {
                string path = StoragePaths.ProfilesFile;
                string json = JsonConvert.SerializeObject(Items, Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);   // atomic swap
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Debug.WriteLine("[PROFILES] save failed: " + ex.Message); }
        }

        public DeviceProfile Find(string id) => Items.FirstOrDefault(c => c.Id == id);

        public void AddOrUpdate(DeviceProfile p)
        {
            if (p == null) return;
            int i = Items.FindIndex(c => c.Id == p.Id);
            if (i >= 0) Items[i] = p; else Items.Add(p);
            Save();
        }

        public void Remove(string id)
        {
            Items.RemoveAll(c => c.Id == id);
            Save();
        }
    }
}
