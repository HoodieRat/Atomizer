// Sample input for AtomizeJs (Public Domain)
// This file is intentionally small and permissively licensed for demos.

export function greet(name) {
  return `Hello, ${name}!`;
}

export function shout(s) {
  return greet(s.toUpperCase());
}
