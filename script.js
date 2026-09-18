const form = document.querySelector("#login-form");
const emailInput = document.querySelector("#email");
const passwordInput = document.querySelector("#password");
const emailError = document.querySelector("#email-error");
const passwordError = document.querySelector("#password-error");
const formMessage = document.querySelector("#form-message");
const passwordToggle = document.querySelector("#password-toggle");

function setError(input, messageElement, message) {
  input.closest(".field-group").classList.toggle("has-error", Boolean(message));
  messageElement.textContent = message;
}

function validateForm() {
  let isValid = true;
  const email = emailInput.value.trim();
  const password = passwordInput.value;

  if (!email) {
    setError(emailInput, emailError, "Please enter your email address.");
    isValid = false;
  } else if (!emailInput.validity.valid) {
    setError(emailInput, emailError, "Please enter a valid email address.");
    isValid = false;
  } else {
    setError(emailInput, emailError, "");
  }

  if (!password) {
    setError(passwordInput, passwordError, "Please enter your password.");
    isValid = false;
  } else if (password.length < 6) {
    setError(passwordInput, passwordError, "Password must be at least 6 characters.");
    isValid = false;
  } else {
    setError(passwordInput, passwordError, "");
  }

  return isValid;
}

passwordToggle.addEventListener("click", () => {
  const isPasswordVisible = passwordInput.type === "text";
  passwordInput.type = isPasswordVisible ? "password" : "text";
  passwordToggle.textContent = isPasswordVisible ? "Show" : "Hide";
  passwordToggle.setAttribute(
    "aria-label",
    isPasswordVisible ? "Show password" : "Hide password"
  );
});

form.addEventListener("submit", (event) => {
  event.preventDefault();
  formMessage.textContent = "";

  if (!validateForm()) {
    return;
  }

  formMessage.textContent = "You’re signed in! (Demo response)";
  form.reset();
});

emailInput.addEventListener("input", () => {
  if (emailInput.value) {
    setError(emailInput, emailError, "");
  }
});

passwordInput.addEventListener("input", () => {
  if (passwordInput.value) {
    setError(passwordInput, passwordError, "");
  }
});
