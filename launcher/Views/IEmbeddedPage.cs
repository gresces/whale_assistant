using System;
using System.Threading.Tasks;

namespace WhaleLauncher.Views
{
    /// <summary>
    /// 宿主页内嵌旧页面时，隐藏旧页自己的大标题和页边距，避免「标题套标题」。
    /// 只调整外壳，不动页面的 x:Name、事件和业务逻辑。
    /// </summary>
    public interface IEmbeddedPage
    {
        bool Embedded { set; }
    }

    /// <summary>
    /// 功能页签上的状态摘要（「已启用 · 12 张」「提醒开 · 搭话关」）。
    /// 摘要由各页自己算：数据只有页面知道，宿主页不复制业务判断。
    /// </summary>
    public interface ITabStatus
    {
        string TabStatus { get; }
        event Action TabStatusChanged;

        /// <summary>只取摘要需要的最小数据，不加载整页（也不会重建列表 / 缩略图）。</summary>
        Task RefreshTabStatusAsync();
    }
}
