"""Read an accepted answer, or synthesize and validate without changing state."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.manager import ValidationOutcome, validate_candidate
from src.solver import create_solver_algorithm
from src.solver_framework import PuzzleRepository, SolverFramework


def obtain_candidate(problem_id: str, puzzle_directory: Path, state_directory: Path) -> dict:
    repository = PuzzleRepository(puzzle_directory)
    puzzle = repository.resolve(problem_id)
    state_path = state_directory / f"{problem_id}.solve"
    if state_path.exists():
        state = json.loads(state_path.read_text(encoding="utf-8"))
        validation = state.get("local_validation", {})
        code = state.get("candidate")
        if (
            state.get("problem_id") == problem_id
            and state.get("status") == "accepted"
            and validation.get("passed") is True
            and isinstance(code, str)
            and len(code.splitlines()) <= puzzle.min_lines
        ):
            return {"problem_id": problem_id, "code": code, "source": "verified"}

    result = SolverFramework(puzzle_repository=repository).solve(
        problem_id, create_solver_algorithm
    )
    validation = validate_candidate(puzzle, result.submitted_code)
    if validation.outcome is not ValidationOutcome.PASS:
        raise RuntimeError(f"candidate validation failed: {validation.outcome.value}")
    return {"problem_id": problem_id, "code": result.submitted_code, "source": "synthesized"}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("problem_id")
    parser.add_argument("--puzzle-directory", type=Path, default=ROOT / "test_data")
    parser.add_argument("--state-directory", type=Path, default=ROOT / "test_output")
    args = parser.parse_args()
    try:
        payload = obtain_candidate(args.problem_id, args.puzzle_directory, args.state_directory)
    except Exception as exc:
        print(json.dumps({"error": f"{type(exc).__name__}: {exc}"}, ensure_ascii=True))
        return 1
    print(json.dumps(payload, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
