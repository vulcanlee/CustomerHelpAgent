using System.Text.Json;
using AntDesign.Core.JsInterop.ObservableApi;
using AntDesign.JsInterop;
using Microsoft.AspNetCore.Components;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// bUnit 專用的「什麼都不做」DOM 事件監聽器，由 <see cref="ComponentTestBase"/> 取代 AntDesign 內建的 <c>DomEventListener</c>。
///
/// 為什麼要換掉：AntDesign 1.6.0 的 <c>DomEventListener</c> 會把共用訂閱記在同一個測試環境的
/// <c>DomEventService</c>（每個 BunitContext 一份）裡，訂閱清單是一般 <c>List</c>；元件非同步釋放時，
/// 多個釋放接續會同時列舉與修改同一份清單，完整測試並行負載高時偶發
/// 「Collection was modified」（約 1/3 次完整執行失敗）。
/// bUnit 的 JSInterop 是 Loose 模式，瀏覽器 DOM 事件本來就不會觸發，換成不做事的版本不影響任何斷言。
/// </summary>
internal sealed class NoopDomEventListener : IDomEventListener
{
    public void AddEventListenerToFirstChild(object dom, string eventName, Action<JsonElement> callback, bool preventDefault = false) { }
    public void AddEventListenerToFirstChild<T>(object dom, string eventName, Action<T> callback, bool preventDefault = false) { }
    public void AddExclusive<T>(object dom, string eventName, Action<T> callback, bool register = true, bool preventDefault = false) { }
    public void AddExclusive<T>(object dom, string eventName, Func<T, Task> callback, bool register = true, bool preventDefault = false) { }
    public ValueTask AddResizeObserver(ElementReference dom, Action<List<ResizeObserverEntry>> callback) => ValueTask.CompletedTask;
    public ValueTask AddResizeObserver(ElementReference dom, Func<List<ResizeObserverEntry>, Task> callback) => ValueTask.CompletedTask;
    public void AddShared<T>(object dom, string eventName, Action<T> callback, bool preventDefault = false) { }
    public void AddShared<T>(object dom, string eventName, Func<T, Task> callback, bool preventDefault = false) { }
    public ValueTask DisconnectResizeObserver(ElementReference dom) => ValueTask.CompletedTask;
    public void DisposeExclusive() { }
    public ValueTask DisposeResizeObserver(ElementReference dom) => ValueTask.CompletedTask;
    public void DisposeShared() { }
    public void RemoveExclusive(object dom, string eventName) { }
    public ValueTask RemoveResizeObserver(ElementReference dom, Action<List<ResizeObserverEntry>> callback) => ValueTask.CompletedTask;
    public ValueTask RemoveResizeObserver(ElementReference dom, Func<List<ResizeObserverEntry>, Task> callback) => ValueTask.CompletedTask;
    public void RemoveShared<T>(object dom, string eventName, Action<T> callback) { }
    public void RemoveShared<T>(object dom, string eventName, Func<T, Task> callback) { }
    public void Dispose() { }
}
