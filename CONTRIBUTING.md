# Contributing to Treemon

Thank you for your interest in contributing to Treemon!

## Getting Started

1. Clone the repository
2. Run `npm install` to install dependencies
3. Use `dotnet build treemon.slnx` to build the project

## Development Setup

The project requires:
- .NET SDK 9.0
- Node.js 20+
- npm

### Running Locally

Start the dev server with hot reload:

```bash
./treemon.ps1 dev "path/to/your/worktrees"
```

This launches the backend on port 5001 and Vite on port 5174.

## Code Style

- F# code follows functional-first patterns
- Prefer immutable data and pure functions
- Use pipe-forward operator for data transformations
- Pattern matching over if/else chains for DU handling

## Testing

Run all tests:

```bash
dotnet test src/Tests/Tests.fsproj
```

Run only fast tests:

```bash
dotnet test src/Tests/Tests.fsproj --filter "Category=Fast"
```

## Pull Requests

- Create a feature branch from `main`
- Write tests for new functionality
- Ensure all existing tests pass
- Keep PRs focused on a single change
- Write clear commit messages

## Architecture

The project uses a client-server architecture:
- **Server**: F# with Saturn/Giraffe, polls external tools (git, az, gh) on a schedule
- **Client**: F# compiled to JS via Fable, uses Elmish MVU pattern with React

Data flows from external tools -> server polling -> in-memory state -> API response -> client render.

## Reporting Issues

Please use GitHub Issues to report bugs or request features. Include:
- Steps to reproduce
- Expected vs actual behavior
- Environment details (OS, .NET version, Node version)
