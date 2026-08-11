// Global Usings for OneCode.App
// Reduces repetitive using declarations across the project.
// ImplicitUsings (enabled in Directory.Build.props) already covers:
//   System, System.Collections.Generic, System.IO, System.Linq,
//   System.Net.Http, System.Threading, System.Threading.Tasks
//
// Note: Tui-specific global usings are in Tui/TuiGlobalUsings.cs

global using Microsoft.Extensions.Logging;
global using OneCode.Core.Commands;
global using OneCode.Core.Domain;
global using OneCode.Core.Hooks;
global using OneCode.Core.Permissions;
global using OneCode.Core.Tools;
global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Globalization;
global using System.Text.Json;
global using Command = OneCode.Core.Commands.Command;
