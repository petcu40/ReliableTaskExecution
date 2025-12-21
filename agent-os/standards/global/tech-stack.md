## Tech stack

### Framework & Runtime
- **Application Framework:** .Net 8.0
- **Language/Runtime:** C#
- **Package Manager:** nuget manager

### Code Standard
- **Formatting:** StyleCop.Analyzers, StyleCop.Analyzers.Unstable, with default options in a stylecop.json file
- **Analyzers:** Microsoft.VisualStudio.Threading.Analyzers

### Libraries
- **Resilience and transient-fault-handling library:** Polly (see https://github.com/App-vNext/Polly)
- **JSON Parsing:** Newtonsoft.Json

### Database & Storage
- **Database:** SQL Azure

### Testing & Quality
- **Test Framework:** Microsoft.NET.Test.Sdk, MSTest.TestAdapter, MSTest.TestFramework, FluentAssertions, Moq

### General Directives
- Use the solution item directory.packages.props to manage nuget versions centrally (ManagePackageVersionsCentrally)
- For build targets and build props defaults use solution items directory.build.props and directory.build.targets
