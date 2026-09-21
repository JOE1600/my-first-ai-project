const menuToggle = document.querySelector(".menu-toggle");
const nav = document.querySelector("nav");
const reserveButton = document.querySelector("#reserve-button");
const reserveMessage = document.querySelector("#reserve-message");

menuToggle?.addEventListener("click", () => {
  const open = menuToggle.getAttribute("aria-expanded") === "true";
  menuToggle.setAttribute("aria-expanded", String(!open));
  nav.classList.toggle("is-open", !open);
});

document.querySelectorAll("nav a").forEach((link) => {
  link.addEventListener("click", () => {
    nav.classList.remove("is-open");
    menuToggle?.setAttribute("aria-expanded", "false");
  });
});

reserveButton?.addEventListener("click", () => {
  reserveMessage.textContent = "Thank you — our Boxwood team will be in touch to confirm your game night.";
  reserveButton.textContent = "Request received ✓";
  reserveButton.disabled = true;
});
