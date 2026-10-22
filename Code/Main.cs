using NeoModLoader.api;

namespace WorldboxGlobe
{
    // 外壳：NML 加载的入口。功能都在 Core 里，开发时由 DevReload 热替换
    public class Main : BasicMod<Main>
    {
        protected override void OnModLoad()
        {
            string folder = GetDeclaration().FolderPath;
            Core.Start(folder);
            DevReload.Install(gameObject, folder);
            LogInfo("星球 loaded");
        }
    }
}
