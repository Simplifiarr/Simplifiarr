// Tests point the app's (process-wide) data folder at their own temp folder,
// so they must not run at the same time as each other.
[assembly: DoNotParallelize]
