# Feature: Translate Wise into Shell Commands

This feature allows users to input a natural language query (a "wise") and convert it into a valid shell command. For example:

**Input:** "I would like to get a list of all files where the content contains Hi Emiel"
**Output:** `grep -rl "Hi Emiel" .`

The tool parses the input, identifies the intent, and generates the corresponding shell command. This enables users to perform complex file operations and searches using simple, natural language queries.

Other possible use cases include:
- Searching for specific strings in files
- Listing files with certain patterns
- Executing commands based on descriptive text

This feature enhances usability by bridging the gap between natural language and command-line interfaces.