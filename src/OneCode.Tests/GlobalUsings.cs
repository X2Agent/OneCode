// Global Usings for OneCode.Tests
// Reduces repetitive using declarations across the test project.
// ImplicitUsings (enabled in Directory.Build.props) already covers:
//   System, System.Collections.Generic, System.IO, System.Linq,
//   System.Net.Http, System.Threading, System.Threading.Tasks

global using System.Globalization;
global using FluentAssertions;
global using Xunit;

// Test classes that read or mutate Environment.CurrentDirectory must not run in
// parallel with each other. This collection serializes them to prevent interference.
[CollectionDefinition(nameof(CurrentDirectoryCollection))]
public sealed class CurrentDirectoryCollection { }
