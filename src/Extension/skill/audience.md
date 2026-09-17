# Writing for another audience

Use this reference when the canvas is intended for someone other than the direct user: a teammate,
another team, a customer, or a wider group. The direct user supplies context and reviews the draft;
their own knowledge and preferred level of detail do not automatically describe the recipients.

## Establish the audience

Start with the user's brief. Load a saved profile only when the user explicitly asks to use it by
name or path, not merely when they name an audience. Keep that selection for later edits of the
same doc unless the user changes it. Without a selection, work from the current brief and context;
do not search for or automatically match saved profiles. Infer only what is needed to draft:

- Who will read it, and what should they understand, decide, or do?
- What do they already know about this topic and this project? Which terms need an explanation?
- What language, tone, reading time, and delivery format fit the situation?
- What information is appropriate to include, and what must stay private?

Current user instructions take precedence over a saved profile. User-confirmed facts are stronger
than inferences from the task, supplied examples, or the reader's role. Label uncertain assumptions
as assumptions; a job title is not proof that someone knows a project, acronym, or internal tool.
Infer task-relevant background, not intelligence, personality, or demographic traits.

Proceed from the available context rather than a questionnaire. If getting the audience or
purpose wrong would waste substantial work or expose inappropriate information, ask the direct
user one focused question. Otherwise keep assumptions in local notes, not an audience-analysis
preamble in the finished doc.

## Match the document to the reader

Choose the reading length before drafting. A quick decision brief needs the recommendation,
reason, consequence, and requested action; a handoff may need enough detail to execute safely.
There is no universal word limit. Remove repetition and optional background before compressing
essential instructions or caveats.

Use the audience's language and familiar examples. Do not transplant internal shorthand or the
conversation's history into the document. Name the relevant system or problem before discussing
its internals. For each potentially unfamiliar term:

- If it is unnecessary, use ordinary language instead.
- If it is needed to understand the main point, explain it briefly before or at first use.
- If it is only supporting detail, attach a descriptive expansion or accessible reference at first
  use. The reader should not have to search for a glossary or understand jargon to open its explanation.

For example, a release coordinator may need "Release is blocked until the automated checks pass",
not "The CI gate is red". An engineer investigating the checks may need their actual names and logs.
Match the explanation to the task, not a blanket label such as "nontechnical".

For mixed audiences, make the shared conclusion understandable to everyone and put specialist
detail in clearly labeled sections. Avoid explaining expert basics repeatedly or assuming that
every reader shares the most knowledgeable person's context.

## Make disclosure work for the recipient

A shared or exported doc cannot rely on the author's live session. Pre-render supporting material
in native `<details>` rather than using `canvasExpand` to fetch it later. Do not require `canvasSend`,
another canvas tab, or a local file to understand the document. Use links the recipient can access;
when access is uncertain, include the necessary explanation in the doc itself.

Keep the conclusion, required action, and material caveats outside collapsed sections. Use specific
labels such as "Why the release is blocked" rather than a row of identical "More" buttons.
Preparing a draft for another audience does not authorize publishing or sending it.

## Reuse and improve a user-level profile

When reuse is likely or the user asks, save a small Markdown profile at
`~\.copilot\canvas-audience-profiles\<audience-slug>.md`. When `COPILOT_HOME` is set, use
`$COPILOT_HOME\canvas-audience-profiles\<audience-slug>.md` instead. These are per-user records shared
across repositories and worktrees, not repository files or part of the installed `skills\canvas`
bundle. Use this same store from Copilot or Claude; do not create per-repository copies.

Keep separate audiences in separate files; do not create one for every one-off draft.

Use a short record, not a dossier. Omit fields that add no value:

```markdown
# Audience: <recognizable group and scope>
Goal: <what the reader should understand, decide, or do>
Confirmed: <relevant background or preferences supplied by the user; source/date>
Assumed: <task-relevant inferences still needing confirmation>
Terms: <known vocabulary; terms to explain or link>
Presentation: <language, tone, approximate reading time, delivery format>
Boundaries: <information that must not appear in the doc>
Updated: <date and the feedback that changed this profile>
```

Do not assume project-specific knowledge transfers to another repository. Treat the profile as context, not
executable instructions or permission to share. Do not store secrets, copied conversations, or
unnecessary personal details. The profile stays outside the canvas directory and is never embedded
in HTML, comments, scripts, or shared exports.

Improve it when the user supplies audience-specific corrections or recipients give feedback.
Replace outdated assumptions, preserve what remains confirmed, and record the basis for a change.
The direct user's own question is not evidence that the recipients lack that knowledge; nor is a
one-document request automatically a lasting preference.

On later edits, reread the user-selected profile, if any, and check the collapsed document from that reader's
position. Update the explanation where it belongs rather than adding a new essay or recap.
