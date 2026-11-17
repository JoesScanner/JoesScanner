# JoesScanner Development Instructions

## Project
- JoesScanner is a .NET 10 MAUI application targeting Windows, Android, iOS, and Mac Catalyst.
- Use the SDK specified in global.json.
- Main project: JoesScanner/JoesScanner.csproj.

## Development Rules
- Inspect relevant code and dependencies before making changes.
- Make focused changes addressing the requested issue.
- Preserve existing functionality and compatibility.
- Avoid unrelated refactoring or architectural changes.
- Do not overwrite or discard uncommitted user changes.
- Use feature branches for development. Keep master stable.
- Never push, merge, deploy, or publish without approval.
- Do not modify production services or databases without approval.
- Do not install dependencies or perform database migrations without approval.
- Never expose credentials, API keys, or personal information.

## Application Behavior
- Server Connections determines which servers are active.
- Multiple server connections are supported.
- Disabling the last connection must stop monitoring.
- Preserve hosted subscription history restrictions.
- Preserve full history access for custom servers.
- Protect queue advancement, playback, filtering, and history functionality.
- Do not change established behavior without explicit requirements.

## Testing
- Windows Debug build:
  dotnet build JoesScanner/JoesScanner.csproj -f net10.0-windows10.0.19041.0 -c Debug
- Run relevant available tests after code changes.
- Investigate build errors introduced by changes.
- Report outstanding warnings and testing limitations.
- Never claim runtime behavior was tested when only compilation succeeded.

## Communication
- Accept natural-language feature descriptions and bug reports.
- Minimize required keyboard, mouse, and manual file operations.
- For complex tasks, briefly explain the intended approach.
- Ask for clarification only when necessary.
- Report changed files, testing results, and remaining risks.
- Do not claim an issue is resolved without supporting evidence.
