// The docs sidebar's one piece of behaviour: a mobile disclosure.
//
// The stylesheet keeps the nav list visible and stacked when this script has not run (`.no-js`),
// so a reader without JavaScript gets a longer page rather than an unreachable index.
(function () {
  document.documentElement.classList.remove('no-js');

  var button = document.querySelector('.nav-btn');
  var nav = document.getElementById('docs-nav');
  if (!button || !nav) return;

  function setOpen(open) {
    document.body.classList.toggle('nav-open', open);
    button.setAttribute('aria-expanded', String(open));
  }

  button.addEventListener('click', function () {
    setOpen(!document.body.classList.contains('nav-open'));
  });

  // Following a link should not leave the panel covering the page it just opened.
  nav.addEventListener('click', function (event) {
    if (event.target.closest('a')) setOpen(false);
  });

  document.addEventListener('keydown', function (event) {
    if (event.key === 'Escape' && document.body.classList.contains('nav-open')) {
      setOpen(false);
      button.focus();
    }
  });
})();
