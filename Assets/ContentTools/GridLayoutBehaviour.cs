using UnityEngine;

namespace ContentTools
{
    [ExecuteInEditMode]
    public class GridLayoutBehaviour : MonoBehaviour
    {
        public int columns = 1;
        public int rows = 1;
        public float cellSize = 1;
        public float cellSpacing = 1;
        public Vector2 offset = Vector2.zero;

        public void LayoutChildren()
        {
            // Check if there are any children
            int childCount = transform.childCount;
            if (childCount == 0) return;

            // Cache the starting position based on offset
            Vector3 startingPosition = new Vector3(offset.x, offset.y, 0);

            for (int i = 0; i < childCount; i++)
            {
                // Get the current child
                Transform child = transform.GetChild(i);

                // Compute the column and row for the current index
                int column = i % columns;
                int row = i / columns;

                // Compute the position with spacing and cell size
                Vector3 position = startingPosition + new Vector3(
                    column * (cellSize + cellSpacing),
                    -row * (cellSize + cellSpacing), // Negative Y to stack rows downward
                    0
                );

                // Set the position of the child
                child.localPosition = position;
            }
        }

        void Update()
        {
            LayoutChildren();
        }
    }
}