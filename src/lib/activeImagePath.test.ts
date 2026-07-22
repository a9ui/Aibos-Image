import { describe, expect, it } from "vitest";

import {
  canonicalImagePathKey,
  findActiveIndexedImagePath,
} from "./activeImagePath";

describe("active image path resolution", () => {
  it("matches Windows image identity case-insensitively and returns indexed spelling", () => {
    const indexedPath = "D:\\Images\\Target.PNG";

    expect(
      findActiveIndexedImagePath(
        "d:\\images\\target.png",
        ["D:\\Images\\Other.png", indexedPath],
        "win32",
      ),
    ).toBe(indexedPath);
    expect(canonicalImagePathKey(indexedPath, "win32")).toBe(
      "d:\\images\\target.png",
    );
  });

  it("rejects paths that are not exact active-index identities", () => {
    expect(
      findActiveIndexedImagePath(
        "/images/target.png",
        ["/images/other.png"],
        "linux",
      ),
    ).toBeNull();
    expect(
      findActiveIndexedImagePath(
        "/images/target.png",
        ["/images/Target.png"],
        "linux",
      ),
    ).toBeNull();
  });
});
