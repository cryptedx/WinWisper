# Taskboard manual verification checklist

Use this checklist after starting the Windows build with the seeded Taskboard data.

- [ ] The top-right `+ Task` button opens quick task entry; submitting a title creates a task in To Do.
- [ ] Task cards fill the complete width of their column and keep one consistent width.
- [ ] To Do, Doing, Done, Archived, and Trash contain representative seeded tasks, including long titles, descriptions, due dates, tags, and an untagged task.
- [ ] The save status is understandable. A successful edit shows a saved state; a real failed save offers a meaningful recovery action.
- [ ] Dragging a task reorders it within a column and moving it across columns persists the new status and order.
- [ ] Dragging a tag from the top row onto a task assigns it immediately, shows the tag on the card, and persists it after restart; dropping the same tag again does not duplicate it.
- [ ] Clicking another task while the details drawer is open switches directly to that task without closing the drawer first.
- [ ] Manage Tags shows each color option with its matching color swatch before selection.
- [ ] Deleting a tag first shows a confirmation prompt with the affected-task count.
- [ ] Cancelling the confirmation leaves the tag and all assignments unchanged; confirming removes the tag assignments.
- [ ] The tag name input aligns with the tag rows, and the Add button aligns with the color controls.
- [ ] Empty-column messaging and controls have visible spacing from the bottom edge.
- [ ] No lower-left `+ Task` button is present; task creation is available from the top-right button.
- [ ] Opening an untagged task shows parser-based tag suggestions in the details drawer; choosing one adds the tag.
- [ ] Tag suggestions stay anchored to the tag field and disappear after selection or when the drawer closes.
- [ ] The board menu can export a recovery copy and import it again. Import confirmation is shown, the current file is backed up, and the imported task data replaces the board after confirmation.

## Automated evidence

- [ ] The focused Taskboard core and end-to-end tests pass on `windows-vm`.
- [ ] The complete test suite passes on `windows-vm`.
- [ ] The current Windows build opens with the seeded data and the interactive checks above are completed.
