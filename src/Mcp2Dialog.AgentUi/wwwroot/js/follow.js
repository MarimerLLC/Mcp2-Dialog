// Keeps a scrolling panel pinned to its newest content, unless the user has scrolled up to read something older.
export function follow(element) {
  if (!element) return;
  if (element.dataset.follow === undefined) {
    element.dataset.follow = "1";
    let lastTop = element.scrollTop;
    element.addEventListener("scroll", () => {
      // New content can arrive between a programmatic scroll and its scroll event, so only an upward scroll (the
      // user's) stops following, and reaching the bottom again resumes it.
      if (element.scrollTop < lastTop - 5) element.dataset.follow = "0";
      if (element.scrollHeight - element.scrollTop - element.clientHeight < 60) element.dataset.follow = "1";
      lastTop = element.scrollTop;
    });
  }
  if (element.dataset.follow === "1") {
    element.scrollTop = element.scrollHeight;
  }
}
