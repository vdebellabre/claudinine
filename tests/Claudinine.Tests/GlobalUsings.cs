// Namespaces used by two or more files in this project. Single-use namespaces
// stay as file-level usings so their one call site still declares what it needs.
//
// Nothing already global belongs here: ImplicitUsings covers System, System.IO,
// System.Linq, System.Collections.Generic, System.Threading[.Tasks], and the
// TUnit package contributes its own globals (TUnit.Core, TUnit.Assertions,
// TUnit.Assertions.Extensions).

global using System.Text;
global using System.Text.Json.Nodes;

global using Claudinine.Mirror;
global using Claudinine.Rules;
