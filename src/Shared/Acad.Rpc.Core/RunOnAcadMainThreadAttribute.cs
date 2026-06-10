using System;

namespace Acad.Rpc.Core;

/// <summary>
/// Marks a tool method that must be invoked on AutoCAD's main thread.
/// The host wraps invocation through <see cref="IAcadMainThreadDispatcher"/>
/// when this attribute is present.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RunOnAcadMainThreadAttribute : Attribute { }
