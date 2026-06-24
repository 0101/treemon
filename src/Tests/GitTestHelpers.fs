module Tests.GitTestHelpers

open System.Diagnostics
open System.IO
open NUnit.Framework

/// Runs `git args` in `workingDir`, returning the exit code and trimmed stdout.
let runGit (workingDir: string) (args: string) =
    let psi =
        ProcessStartInfo(
            "git",
            args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        )

    use proc = Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    proc.StandardError.ReadToEnd() |> ignore
    proc.WaitForExit()
    proc.ExitCode, stdout.Trim()

/// Runs `git args`, asserting it exits 0.
let gitAssert (workingDir: string) (args: string) =
    let exitCode, _ = runGit workingDir args
    Assert.That(exitCode, Is.EqualTo(0), $"git {args} failed (exit {exitCode})")

/// Runs `git args` and returns its trimmed stdout.
let gitOut (workingDir: string) (args: string) =
    let _, stdout = runGit workingDir args
    stdout

/// Creates a fresh git repo with an identity configured (no commits yet).
let initRepo (repoDir: string) =
    Directory.CreateDirectory(repoDir) |> ignore
    gitAssert repoDir "init"
    gitAssert repoDir "config user.name test"
    gitAssert repoDir "config user.email test@test.com"

/// Creates a fresh repo with a single commit and a `main` branch checked out.
let initRepoOnMain (repoDir: string) =
    initRepo repoDir
    gitAssert repoDir "commit --allow-empty -m init"
    gitAssert repoDir "branch -M main"

/// Creates a `repo` on main with a bare `origin` remote that has `main` pushed,
/// so both a local `main` and `refs/remotes/origin/main` exist.
let initRepoWithOrigin (tempDir: string) =
    let repoDir = Path.Combine(tempDir, "repo")
    let originDir = Path.Combine(tempDir, "origin.git")
    initRepoOnMain repoDir
    Directory.CreateDirectory(originDir) |> ignore
    gitAssert originDir "init --bare"
    gitAssert repoDir $"remote add origin \"{originDir}\""
    gitAssert repoDir "push origin main"
    repoDir, originDir
