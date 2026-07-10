module Tests.FileUtilsTests

open System
open System.IO
open System.Text
open NUnit.Framework
open Server

// Covers FileUtils.readByteRangeLines, the append-aware read the incremental session scan folds each
// cycle. The focus is the chunked/streaming read that replaced a single whole-range allocation: it
// must (a) read ranges far larger than one 64 KB chunk without an int32-overflow or LOH spike, (b)
// keep exact whole-buffer semantics — complete lines only, a partial trailing line left unconsumed at
// the last-newline offset — and (c) reassemble multi-byte UTF-8 characters that straddle a chunk edge.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ReadByteRangeLinesTests() =

    let chunkSize = 64 * 1024

    let withTempFile (content: string) (action: string -> 'a) =
        TestUtils.withTempFile "fileutils-test" content action

    [<Test>]
    member _.``Reads every complete line across many chunks and consumes the whole file``() =
        // ~270 KB — several 64 KB read chunks — with every line newline-terminated. This is the
        // large-range read the finding is about; the pre-fix single Array.zeroCreate of this range was
        // the LOH spike, and its `int (endOffset - startOffset)` cast the overflow on a multi-GB file.
        let expected =
            List.init 2000 (fun i -> $"{{\"i\":{i + 1},\"pad\":\"{String('x', 120)}\"}}")
        let content = (String.concat "\n" expected) + "\n"

        withTempFile content (fun path ->
            let fileLength = FileInfo(path).Length
            Assert.That(fileLength, Is.GreaterThan(int64 chunkSize)) // proves the range spans >1 chunk

            let lines, offset = FileUtils.readByteRangeLines "test" path 0L fileLength
            Assert.That(lines, Is.EqualTo(expected))
            Assert.That(offset, Is.EqualTo(fileLength)))

    [<Test>]
    member _.``Leaves a partial trailing line unconsumed at the last-newline offset``() =
        let content = "line one\nline two\npartial-not-yet-terminated"

        withTempFile content (fun path ->
            let fileLength = FileInfo(path).Length
            let lines, offset = FileUtils.readByteRangeLines "test" path 0L fileLength

            Assert.That(lines, Is.EqualTo([ "line one"; "line two" ]))
            // Offset stops just past the second '\n'; the partial tail stays unconsumed for a later append.
            let consumed = int64 (Encoding.UTF8.GetByteCount("line one\nline two\n"))
            Assert.That(offset, Is.EqualTo(consumed))
            Assert.That(offset, Is.LessThan(fileLength)))

    [<Test>]
    member _.``A range holding no newline yields no lines and does not advance the offset``() =
        let content = "a single unterminated line with no newline byte"

        withTempFile content (fun path ->
            let lines, offset = FileUtils.readByteRangeLines "test" path 0L (FileInfo(path).Length)
            Assert.That(lines, Is.Empty)
            Assert.That(offset, Is.EqualTo(0L)))

    [<Test>]
    member _.``A multi-byte UTF-8 character straddling a chunk boundary is decoded intact``() =
        // The 4-byte emoji's bytes span the 64 KB read-chunk edge, so a naive per-chunk decode would
        // split the character and emit U+FFFD. Getting the original line back pins the carry-across-
        // chunk-boundary behavior of the streaming read.
        let filler = String('a', chunkSize - 2)
        let line = filler + "🎉" + "END"
        let content = line + "\n"

        withTempFile content (fun path ->
            let fileLength = FileInfo(path).Length
            let lines, offset = FileUtils.readByteRangeLines "test" path 0L fileLength

            Assert.That(lines, Is.EqualTo([ line ]))
            Assert.That(offset, Is.EqualTo(fileLength)))

    [<Test>]
    member _.``Reads only the requested sub-range and returns absolute offsets``() =
        // A non-zero startOffset (the incremental append path): the returned offset is absolute, and
        // lines before startOffset are not re-read.
        let content = "alpha\nbeta\ngamma\ndelta\n"

        withTempFile content (fun path ->
            let startOffset = int64 (Encoding.UTF8.GetByteCount("alpha\nbeta\n"))
            let endOffset = FileInfo(path).Length
            let lines, offset = FileUtils.readByteRangeLines "test" path startOffset endOffset

            Assert.That(lines, Is.EqualTo([ "gamma"; "delta" ]))
            Assert.That(offset, Is.EqualTo(endOffset)))
