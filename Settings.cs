using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityModManagerNet;

namespace WorkshopInstaller {
    public class Settings : UnityModManager.ModSettings {
        public bool shouldAutoUpdate = true;
        public SerializableDictionary<PublishedFileId_t, string> installedItems = new();
        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
    }
}
